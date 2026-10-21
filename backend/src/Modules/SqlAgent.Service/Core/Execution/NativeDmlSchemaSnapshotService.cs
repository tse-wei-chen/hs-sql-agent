using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Provider-native, opt-in schema freshness implementation for a single resolved physical
/// DML target. Uses each provider's existing planning metadata SQL on preview and re-reads
/// the exact target inside the commit transaction. Unsupported multi-source shapes fail closed.
/// The native column type, PK identity and ordinal are bound into Snapshot.Version even
/// though the portable compiler conservatively treats their expression type as Unknown.
/// </summary>
public sealed class NativeDmlSchemaSnapshotService :
    IDmlSchemaSnapshotSource, IDmlSchemaSnapshotFreshnessVerifier
{
    private sealed record Readers(
        IProviderConnectionDmlPlanningMetadataReader Preview,
        IProviderTransactionDmlPlanningMetadataReader Commit);

    private static readonly IReadOnlyDictionary<SqlAgentToolType, Readers> NativeReaders =
        new Dictionary<SqlAgentToolType, Readers>
        {
            [SqlAgentToolType.Postgres] = ReadersFor(new HsSqlAgent.Provider.PostgreSql.PostgresProvider()),
            [SqlAgentToolType.MySQL] = ReadersFor(new HsSqlAgent.Provider.MySql.MySqlProvider()),
            [SqlAgentToolType.Sqlite] = ReadersFor(new HsSqlAgent.Provider.Sqlite.SqliteProvider()),
            [SqlAgentToolType.MsSqlServer] = ReadersFor(new HsSqlAgent.Provider.SqlServer.MsSqlServerProvider()),
            [SqlAgentToolType.Oracle] = ReadersFor(new HsSqlAgent.Provider.Oracle.OracleProvider()),
            [SqlAgentToolType.Firebird] = ReadersFor(new HsSqlAgent.Provider.Firebird.FirebirdProvider())
        };

    private static Readers ReadersFor<T>(T native)
        where T : IProviderConnectionDmlPlanningMetadataReader, IProviderTransactionDmlPlanningMetadataReader =>
        new(native, native);

    public async Task<SqlSchemaSnapshot?> CaptureAsync(
        DbConnection connection,
        SqlAgentToolType targetProvider,
        ParsedStatement parsedMutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parsedMutation);
        if (parsedMutation.SourceDialect != targetProvider)
            throw new InvalidOperationException(
                "Native DML schema proofs require identical source and target providers.");

        var target = parsedMutation.Statement switch
        {
            InsertStatement { Source: InsertValuesSource } insert => insert.Target,
            UpdateStatement update when update.From.IsDefaultOrEmpty
                && update.FromSources.IsDefaultOrEmpty => update.Target,
            DeleteStatement delete when delete.Using.IsDefaultOrEmpty
                && delete.UsingSources.IsDefaultOrEmpty => delete.Target,
            _ => throw new InvalidOperationException(
                "Native DML schema proof currently supports single-target INSERT VALUES, UPDATE, and DELETE only.")
        };

        var parts = target.Name.Parts;
        if (parts.Length is < 1 or > 2)
            throw new InvalidOperationException("Native DML schema target must be table or schema.table.");
        var schema = parts.Length == 2 ? parts[0].Value : null;
        var table = parts[^1].Value;

        var readers = ForProvider(targetProvider);
        var matches = await readers.Preview.GetDmlPlanningMetadataAsync(
            connection, schema, table, includeColumns: true,
            cancellationToken: cancellationToken);
        var snapshot = BuildSnapshot(targetProvider, matches);
        if (snapshot is null || targetProvider != SqlAgentToolType.Sqlite)
            return snapshot;

        return await WithSqliteDdlProofAsync(
            connection, transaction: null, snapshot, matches[0].Table, cancellationToken);
    }

    public async Task<string?> ReadCurrentSchemaDigestAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        SqlSchemaSnapshot expectedSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        // Reject cross-connection transactions *before* any provider lock or
        // catalog SQL. Relying on provider validation is not sufficient: a
        // PostgreSQL LOCK TABLE on the wrong connection can execute outside an
        // explicit transaction, yielding a provider-specific error instead.
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException(
                "Schema freshness transaction does not belong to its database connection.");
        if (expectedSnapshot.Provider != provider || expectedSnapshot.Tables.Count != 1)
            return null;

        // Firebird's 'Default' is a catalog-only pseudo-schema, not part
        // of SQL identifier syntax. Other providers require schema.table.
        // Reject dotted physical names that cannot be represented losslessly.
        var parts = expectedSnapshot.Tables[0].Name.Split('.');
        var firebird = provider == SqlAgentToolType.Firebird;
        if (parts.Any(string.IsNullOrWhiteSpace)
            || (firebird ? parts.Length != 1 : parts.Length != 2))
            return null;
        var schema = firebird ? "Default" : parts[0];
        var table = firebird ? parts[0] : parts[1];

        // PostgreSQL catalog reads alone do not pin the physical table schema
        // across the revalidation-to-write gap. ACCESS SHARE is compatible with
        // concurrent DML, but conflicts with ALTER/DROP's ACCESS EXCLUSIVE until
        // this SAME transaction commits or rolls back. Lock before checking the
        // catalog so the checked shape cannot be swapped underneath the write.
        switch (provider)
        {
            case SqlAgentToolType.Postgres:
                await PinPostgresSchemaAsync(
                    connection, transaction, schema, table, cancellationToken);
                break;
            case SqlAgentToolType.MySQL:
                await PinMySqlSchemaAsync(
                    connection, transaction, schema, table, cancellationToken);
                break;
            case SqlAgentToolType.MsSqlServer:
                await PinMsSqlServerSchemaAsync(
                    connection, transaction, schema, table, cancellationToken);
                break;
        }

        var readers = ForProvider(provider);
        var matches = await readers.Commit.GetDmlPlanningMetadataInTransactionAsync(
            connection, transaction, schema, table, includeColumns: true,
            cancellationToken: cancellationToken);
        var snapshot = BuildSnapshot(provider, matches);
        if (snapshot is null)
            return null;

        if (provider == SqlAgentToolType.Sqlite)
            snapshot = await WithSqliteDdlProofAsync(
                connection, transaction, snapshot, matches[0].Table, cancellationToken);

        return snapshot?.Sha256;
    }

    private static async Task PinPostgresSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        // Each identifier is a *single catalog-returned name*, never SQL text.
        // Dot/empty names are already rejected by BuildSnapshot and the caller.
        static string Quote(string identifier) =>
            "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"LOCK TABLE {Quote(schema)}.{Quote(table)} IN ACCESS SHARE MODE";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PinMySqlSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        // MySQL's normal table reference takes a shared metadata lock (MDL),
        // retained for the current explicit transaction. NEVER use LOCK TABLES:
        // that statement can implicitly commit the already-approved DML transaction.
        static string Quote(string identifier) =>
            "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT 1 FROM {Quote(schema)}.{Quote(table)} LIMIT 1";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task PinMsSqlServerSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        // SQL Server Sch-S locks used during statement execution are not enough:
        // they are released at statement completion. TABLOCK + HOLDLOCK retains
        // a TABLE S lock until commit/rollback, conflicting with DDL's Sch-M.
        // This may also block other sessions' DML. Strict mode is opt-in.
        static string Quote(string identifier) =>
            "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT TOP (1) 1 FROM {Quote(schema)}.{Quote(table)} WITH (TABLOCK, HOLDLOCK)";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Bind SQLite's own table/index/trigger DDL to the portable catalog proof.
    /// pragma_table_info deliberately omits generated/hidden columns and the
    /// former name/type/PK digest misses CHECK, DEFAULT, NOT NULL, indexes,
    /// expression indexes and DML trigger changes. sqlite_schema.sql stores
    /// the complete definitions; include every object owned by this main table.
    /// Both preview and commit must use the same scope and canonicalization.
    /// </summary>
    private static async Task<SqlSchemaSnapshot?> WithSqliteDdlProofAsync(
        DbConnection connection,
        DbTransaction? transaction,
        SqlSchemaSnapshot baseSnapshot,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type, name, sql
            FROM main.sqlite_schema
            WHERE tbl_name = $table COLLATE NOCASE
              AND type IN ('table', 'index', 'trigger')
            ORDER BY type COLLATE BINARY, name COLLATE BINARY
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Append(IncrementalHash digest, string text)
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            digest.AppendData(BitConverter.GetBytes(encoded.Length));
            digest.AppendData(encoded);
        }

        Append(hash, "native-sqlite-ddl-v2");
        Append(hash, baseSnapshot.Version);
        var tableDefinitions = 0;
        var objectCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetString(0);
            var name = reader.GetString(1);
            string? ddl = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (kind == "table")
            {
                if (!string.Equals(name, table, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(ddl))
                    return null;
                tableDefinitions++;
            }
            else if (kind == "trigger" && string.IsNullOrWhiteSpace(ddl))
            {
                // Cannot prove a trigger whose source definition is missing.
                return null;
            }

            Append(hash, kind);
            Append(hash, name);
            // SQLite autoindexes legitimately have NULL SQL; their defining
            // UNIQUE/PRIMARY KEY clauses are included in the table SQL.
            Append(hash, ddl is null ? "null" : "sql");
            if (ddl is not null)
                Append(hash, ddl);
            objectCount++;
        }

        if (tableDefinitions != 1 || objectCount == 0)
            return null;
        Append(hash, objectCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var version = "native-dml-schema-v2:" +
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new SqlSchemaSnapshot(
            SqlAgentToolType.Sqlite, version, baseSnapshot.Tables);
    }

    private static Readers ForProvider(SqlAgentToolType provider) =>
        NativeReaders.TryGetValue(provider, out var readers)
            ? readers
            : throw new InvalidOperationException(
                $"Native DML schema metadata is unavailable for provider {provider}.");

    private static SqlSchemaSnapshot? BuildSnapshot(
        SqlAgentToolType provider,
        IReadOnlyList<DatabaseDmlPlanningMetadata> matches)
    {
        if (matches.Count != 1)
            return null;
        var physical = matches[0];
        if (physical.Columns.Count == 0
            || string.IsNullOrWhiteSpace(physical.Schema)
            || string.IsNullOrWhiteSpace(physical.Table)
            || physical.Schema.Contains('.') || physical.Table.Contains('.'))
            return null;

        var columnNames = physical.Columns.Select(column => column.Name).ToArray();
        if (columnNames.Any(string.IsNullOrWhiteSpace)
            || columnNames.Any(name => name.Contains('.'))
            || columnNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columnNames.Length)
            return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Append(IncrementalHash hash, string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            var length = BitConverter.GetBytes(encoded.Length);
            hash.AppendData(length);
            hash.AppendData(encoded);
        }
        Append(hash, "native-dml-schema-v1");
        Append(hash, provider.ToString());
        Append(hash, physical.Schema);
        Append(hash, physical.Table);
        Append(hash, physical.Columns.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var column in physical.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            Append(hash, column.Name);
            Append(hash, column.Type);
            Append(hash, column.IsPrimaryKey ? "pk" : "non-pk");
            Append(hash, column.PrimaryKeyOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
        }

        var version = "native-dml-schema-v1:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        // Firebird has no schema-qualified table syntax. The fixed 'Default'
        // catalog identity remains bound in Version, while the compiler name
        // must match the actual unqualified relation it will render.
        var tableName = provider == SqlAgentToolType.Firebird
            && string.Equals(physical.Schema, "Default", StringComparison.OrdinalIgnoreCase)
                ? physical.Table
                : physical.Schema + "." + physical.Table;
        var table = new SqlSchemaTable(tableName, columnNames);
        return new SqlSchemaSnapshot(provider, version, [table]);
    }
}
