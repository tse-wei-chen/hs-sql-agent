using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Provider-native, opt-in schema freshness implementation for an exact physical
/// DML target. SQLite's bounded INSERT SELECT also attests its one physical
/// source separately. Unsupported general multi-source shapes fail closed.
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

        var sourceTable = parsedMutation.Statement is InsertStatement { Source: InsertQuerySource } sourceInsert
            && targetProvider is SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres
            ? NativeInsertSelectSafety.RequireShape(
                sourceInsert, targetProvider, targetProvider, int.MaxValue - 1).PhysicalSource
            : null;

        var target = parsedMutation.Statement switch
        {
            InsertStatement { Source: InsertValuesSource } insert => insert.Target,
            // The bounded INSERT SELECT attests each physical main source
            // independently; arbitrary sources never enter this path.
            InsertStatement { Source: InsertQuerySource } insert
                when targetProvider is SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres =>
                VerifyBoundedInsertSource(insert, targetProvider),
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
        if (snapshot is null)
            return null;
        if (targetProvider == SqlAgentToolType.Sqlite)
        {
            var targetProof = await WithSqliteDdlProofAsync(
                connection, transaction: null, snapshot, matches[0].Table, cancellationToken);
            if (targetProof is null || sourceTable is null
                || string.Equals(sourceTable, targetProof.Tables[0].Name,
                    StringComparison.OrdinalIgnoreCase))
                return targetProof;

            // Two independently catalog-attested actual main tables; the
            // full source DDL/index/trigger digest cannot be borrowed from
            // the insert target or a virtual Catalog.
            var sourceName = sourceTable["main.".Length..];
            var sourceMatches = await readers.Preview.GetDmlPlanningMetadataAsync(
                connection, "main", sourceName, includeColumns: true,
                cancellationToken: cancellationToken);
            var sourceBase = BuildSnapshot(targetProvider, sourceMatches);
            if (sourceBase is null || sourceBase.Tables.Count != 1
                || !string.Equals(sourceBase.Tables[0].Name, sourceTable,
                    StringComparison.OrdinalIgnoreCase)) return null;
            var sourceProof = await WithSqliteDdlProofAsync(
                connection, transaction: null, sourceBase, sourceName, cancellationToken);
            return sourceProof is null ? null : CombineNativeSourceTarget(targetProof, sourceProof);
        }
        if (targetProvider == SqlAgentToolType.Postgres && sourceTable is not null)
        {
            var physicalTarget = await PostgresInsertSelectCatalogProof.ExtendAsync(
                connection, null, snapshot, matches[0].Schema, matches[0].Table, cancellationToken);
            if (physicalTarget is null) return null;

            // Reuse the exact same attested physical table when source and
            // target coincide; never fabricate a duplicate two-table proof.
            if (string.Equals(sourceTable, physicalTarget.Tables[0].Name, StringComparison.Ordinal))
                return physicalTarget;

            var sourceNames = sourceTable.Split('.');
            if (sourceNames.Length != 2) return null;
            var secondMatches = await readers.Preview.GetDmlPlanningMetadataAsync(
                connection, sourceNames[0], sourceNames[1], includeColumns: true,
                cancellationToken: cancellationToken);
            var secondBase = BuildSnapshot(targetProvider, secondMatches);
            if (secondBase is null || !string.Equals(secondBase.Tables[0].Name,
                    sourceTable, StringComparison.OrdinalIgnoreCase)) return null;
            var physicalSource = await PostgresInsertSelectCatalogProof.ExtendAsync(
                connection, null, secondBase, secondMatches[0].Schema,
                secondMatches[0].Table, cancellationToken);
            return physicalSource is null ? null :
                CombineNativeSourceTarget(physicalTarget, physicalSource);
        }
        if (targetProvider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
            return await NativeDmlSchemaDefinitionProof.ExtendAsync(
                connection, transaction: null, snapshot, matches[0].Schema,
                matches[0].Table, cancellationToken);
        return snapshot;
    }

    private static NamedTableSource VerifyBoundedInsertSource(
        InsertStatement insert, SqlAgentToolType provider)
    {
        NativeInsertSelectSafety.RequireShape(
            insert, provider, provider, maxAffectedRows: int.MaxValue - 1);
        return insert.Target;
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
        if (expectedSnapshot.Provider != provider) return null;
        if (provider == SqlAgentToolType.Postgres
            && expectedSnapshot.Tables.Count == 1
            && expectedSnapshot.Version.StartsWith(
                "native-postgres-insert-source-v1:", StringComparison.Ordinal))
        {
            var expectedTable = expectedSnapshot.Tables[0].Name;
            var names = expectedTable.Split('.');
            if (names.Length != 2 || names.Any(string.IsNullOrWhiteSpace)
                || names[0].StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(names[0], "information_schema", StringComparison.OrdinalIgnoreCase))
                return null;

            // Self-source proof must block all concurrent source writers BEFORE
            // the first catalog read / SERIALIZABLE snapshot is established.
            await PinPostgresInsertSelectAsync(
                connection, transaction, expectedTable, expectedTable, cancellationToken);
            var matches = await ForProvider(provider).Commit.GetDmlPlanningMetadataInTransactionAsync(
                connection, transaction, names[0], names[1], includeColumns: true,
                cancellationToken: cancellationToken);
            var basic = BuildSnapshot(provider, matches);
            if (basic is null || basic.Tables.Count != 1
                || !string.Equals(basic.Tables[0].Name, expectedTable, StringComparison.Ordinal))
                return null;

            var live = await PostgresInsertSelectCatalogProof.ExtendAsync(
                connection, transaction, basic, names[0], names[1], cancellationToken);
            return live?.Sha256;
        }
        if (provider == SqlAgentToolType.Postgres && expectedSnapshot.Tables.Count == 2)
        {
            var tables = expectedSnapshot.Tables;
            var sourceParts = tables[1].Name.Split('.');
            var targetParts = tables[0].Name.Split('.');
            if (sourceParts.Length != 2 || targetParts.Length != 2
                || sourceParts.Any(string.IsNullOrWhiteSpace)
                || targetParts.Any(string.IsNullOrWhiteSpace)
                || !string.Equals(sourceParts[0], targetParts[0], StringComparison.OrdinalIgnoreCase)
                || string.Equals(tables[0].Name, tables[1].Name, StringComparison.OrdinalIgnoreCase)
                || sourceParts[0].StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
                || sourceParts[0] == "information_schema")
                return null;

            // Acquire source SHARE ROW EXCLUSIVE lock BEFORE the first catalog
            // SELECT and before any serializable snapshot materializes.
            // Conflicts with concurrent ROW EXCLUSIVE writes. NOWAIT fails
            // closed instead of waiting on a writer with an older snapshot.
            // Lock the target too, preventing DDL replacement until commit.
            await PinPostgresInsertSelectAsync(
                connection, transaction, tables[1].Name, tables[0].Name, cancellationToken);
            var verified = new List<SqlSchemaSnapshot>(2);
            var nativeReaders = ForProvider(provider);
            foreach (var expected in tables)
            {
                var names = expected.Name.Split('.');
                var currentTables = await nativeReaders.Commit.GetDmlPlanningMetadataInTransactionAsync(
                    connection, transaction, names[0], names[1], includeColumns: true,
                    cancellationToken: cancellationToken);
                var basic = BuildSnapshot(provider, currentTables);
                if (basic is null || !string.Equals(basic.Tables[0].Name,
                        expected.Name, StringComparison.OrdinalIgnoreCase)) return null;
                var definition = await PostgresInsertSelectCatalogProof.ExtendAsync(
                    connection, transaction, basic, names[0], names[1], cancellationToken);
                if (definition is null) return null;
                verified.Add(definition);
            }
            return CombineNativeSourceTarget(verified[0], verified[1])?.Sha256;
        }
        if (provider == SqlAgentToolType.Sqlite && expectedSnapshot.Tables.Count == 2)
        {
            var verified = new List<SqlSchemaSnapshot>(2);
            var current = ForProvider(provider);
            foreach (var tableProof in expectedSnapshot.Tables)
            {
                var names = tableProof.Name.Split('.');
                if (names.Length != 2 || !string.Equals(names[0], "main",
                        StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(names[1])
                    || names[1].StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
                    || verified.Any(x => string.Equals(x.Tables[0].Name, tableProof.Name,
                        StringComparison.OrdinalIgnoreCase))) return null;
                var results = await current.Commit.GetDmlPlanningMetadataInTransactionAsync(
                    connection, transaction, "main", names[1], includeColumns: true,
                    cancellationToken: cancellationToken);
                var baseProof = BuildSnapshot(provider, results);
                if (baseProof is null || baseProof.Tables.Count != 1
                    || !string.Equals(baseProof.Tables[0].Name, tableProof.Name,
                        StringComparison.OrdinalIgnoreCase)) return null;
                var currentProof = await WithSqliteDdlProofAsync(
                    connection, transaction, baseProof, names[1], cancellationToken);
                if (currentProof is null) return null;
                verified.Add(currentProof);
            }
            return CombineNativeSourceTarget(verified[0], verified[1])?.Sha256;
        }
        if (expectedSnapshot.Tables.Count != 1) return null;

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
            case SqlAgentToolType.Oracle:
                await PinOracleSchemaAsync(
                    connection, transaction, schema, table, cancellationToken);
                break;
        }

        // ODP.NET native metadata readers have been observed to end the
        // earlier uncommitted Oracle target transaction. A target-fence
        // admission and even a ROW SHARE table lock disappeared after those
        // catalog reads in real Oracle integration tests. Keep the target
        // transaction/physical DDL lock on this connection, and attest
        // metadata using a separate *same-settings* Oracle connection.
        // The lock is held for the full independent metadata verification,
        // so concurrent ALTER/DROP cannot swap the physical table.
        if (provider == SqlAgentToolType.Oracle)
        {
            if (connection is not ICloneable clonable
                || clonable.Clone() is not DbConnection metadataConnection
                || ReferenceEquals(metadataConnection, connection))
                throw new InvalidOperationException(
                    "Oracle strict DML schema proof requires a separate cloned metadata " +
                    "connection; target transaction isolation cannot be established.");

            await using (metadataConnection)
            {
                await metadataConnection.OpenAsync(cancellationToken);
                if (!string.Equals(metadataConnection.DataSource, connection.DataSource,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(metadataConnection.Database, connection.Database,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Oracle strict DML schema metadata connection resolved to a " +
                        "different target. No DML may run without the same authority.");

                var oracleReaders = ForProvider(provider);
                var oracleMatches = await oracleReaders.Preview.GetDmlPlanningMetadataAsync(
                    metadataConnection, schema, table, includeColumns: true,
                    cancellationToken: cancellationToken);
                var oracleSnapshot = BuildSnapshot(provider, oracleMatches);
                if (oracleSnapshot is null)
                    return null;

                oracleSnapshot = await NativeDmlSchemaDefinitionProof.ExtendAsync(
                    metadataConnection, transaction: null, oracleSnapshot,
                    oracleMatches[0].Schema, oracleMatches[0].Table, cancellationToken);
                return oracleSnapshot?.Sha256;
            }
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
        else if (provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
            snapshot = await NativeDmlSchemaDefinitionProof.ExtendAsync(
                connection, transaction, snapshot, matches[0].Schema,
                matches[0].Table, cancellationToken);

        return snapshot?.Sha256;
    }

    private static async Task PinPostgresInsertSelectAsync(
        DbConnection connection, DbTransaction transaction, string source, string target,
        CancellationToken token)
    {
        static string QuoteQualified(string physical)
        {
            var parts = physical.Split('.');
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Unproven PostgreSQL physical table.");
            return string.Join(".", parts.Select(part =>
                "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        }
        await using var lockSource = connection.CreateCommand();
        lockSource.Transaction = transaction;
        lockSource.CommandText =
            $"LOCK TABLE ONLY {QuoteQualified(source)} IN SHARE ROW EXCLUSIVE MODE NOWAIT";
        await lockSource.ExecuteNonQueryAsync(token);

        // One physical table is one lock, not two round trips or two proof
        // identities. The source lock already excludes DDL on that table.
        if (string.Equals(source, target, StringComparison.Ordinal))
            return;

        await using var lockTarget = connection.CreateCommand();
        lockTarget.Transaction = transaction;
        lockTarget.CommandText =
            $"LOCK TABLE ONLY {QuoteQualified(target)} IN ACCESS SHARE MODE NOWAIT";
        await lockTarget.ExecuteNonQueryAsync(token);
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

    private static async Task PinOracleSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        // Oracle dictionary SELECTs do not protect against concurrent ALTER
        // or DROP: obtain a transaction-held TM row-share table lock first.
        // ROW SHARE permits concurrent ordinary DML but conflicts with
        // exclusive DDL locks. NOWAIT turns contested DDL into fail-closed
        // revalidation rather than an unbounded worker wait.
        // Quote each *catalog-attested* identifier independently.
        static string Quote(string identifier) =>
            "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"LOCK TABLE {Quote(schema)}.{Quote(table)} IN ROW SHARE MODE NOWAIT";
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

    /// <summary>
    /// Bind the exact two native physical SQLite DDL attestations, including
    /// source-only indexes/triggers, to a single immutable two-table scope.
    /// The order is always target first, source second. The same helper is
    /// used at preview capture and inside the held commit transaction.
    /// </summary>
    private static SqlSchemaSnapshot? CombineNativeSourceTarget(
        SqlSchemaSnapshot target, SqlSchemaSnapshot source)
    {
        if (target.Provider != source.Provider
            || target.Provider is not (SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres)
            || target.Tables.Count != 1 || source.Tables.Count != 1
            || string.Equals(target.Tables[0].Name, source.Tables[0].Name,
                StringComparison.OrdinalIgnoreCase)) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Append(IncrementalHash digest, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            digest.AppendData(BitConverter.GetBytes(bytes.Length));
            digest.AppendData(bytes);
        }
        Append(hash, target.Provider == SqlAgentToolType.Sqlite
            ? "native-sqlite-insert-select-dual-ddl-v1"
            : "native-postgres-insert-select-dual-catalog-v1");
        Append(hash, target.Tables[0].Name);
        Append(hash, target.Sha256);
        Append(hash, source.Tables[0].Name);
        Append(hash, source.Sha256);
        var version = (target.Provider == SqlAgentToolType.Sqlite
            ? "native-sqlite-dual-source-v1:"
            : "native-postgres-dual-source-v1:") +
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new SqlSchemaSnapshot(target.Provider,
            version, [target.Tables[0], source.Tables[0]]);
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
