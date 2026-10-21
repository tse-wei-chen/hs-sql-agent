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
        return BuildSnapshot(targetProvider, matches);
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

        var readers = ForProvider(provider);
        var matches = await readers.Commit.GetDmlPlanningMetadataInTransactionAsync(
            connection, transaction, schema, table, includeColumns: true,
            cancellationToken: cancellationToken);
        return BuildSnapshot(provider, matches)?.Sha256;
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
