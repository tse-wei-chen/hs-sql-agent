using System.Data.Common;

namespace HsSqlAgent.Provider.Abstractions;

public sealed record DatabaseTableMetadata(
    string Schema,
    string Table);

public sealed record DatabaseColumnMetadata(
    string Schema,
    string Table,
    string Name,
    string Type,
    bool IsPrimaryKey,
    int? PrimaryKeyOrdinal = null);

public sealed record DatabaseUniqueKeyMetadata(
    string Schema,
    string Table,
    string Name,
    bool IsPrimaryKey,
    IReadOnlyList<string> Columns,
    bool IsPartial = false,
    bool HasExpressions = false,
    bool HasPrefixKeyParts = false,
    bool IsEnforced = true)
{
    public bool IsSimpleEnforcedColumnKey =>
        IsEnforced
        && !IsPartial
        && !HasExpressions
        && !HasPrefixKeyParts
        && Columns.Count > 0;
}

/// <summary>
/// Optional additive metadata capability for resolving an unqualified table name without
/// enumerating every schema. Implementations return every physical match so callers preserve
/// ambiguity detection instead of silently selecting a default schema.
/// </summary>
public interface IProviderTableLookup
{
    Task<IReadOnlyList<DatabaseTableMetadata>> FindTablesAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional additive capability for metadata reads over an already-open provider connection.
/// DML planning uses this to reuse the connection that already established the verified runtime
/// profile instead of opening additional pooled connections for table and column metadata.
/// </summary>
public interface IProviderConnectionMetadataReader
{
    Task<IReadOnlyList<DatabaseTableMetadata>> FindTablesAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
        DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable metadata snapshot used by DML planning. When triggerOperation is requested,
/// HasEnabledDmlTrigger is populated only when the provider can prove trigger metadata completeness;
/// null means the required assurance could not be established and callers must fail closed.
/// </summary>
public sealed record DatabaseDmlPlanningMetadata(
    string Schema,
    string Table,
    IReadOnlyList<DatabaseColumnMetadata> Columns,
    bool? HasEnabledDmlTrigger = null);

/// <summary>
/// Optional additive capability that resolves a DML target and the metadata required by row-impact
/// planning in one provider-native catalog command over an already-open connection.
/// </summary>
public interface IProviderConnectionDmlPlanningMetadataReader
{
    Task<IReadOnlyList<DatabaseDmlPlanningMetadata>> GetDmlPlanningMetadataAsync(
        DbConnection connection,
        string? schema,
        string table,
        bool includeColumns,
        DmlOperation? triggerOperation = null,
        CancellationToken cancellationToken = default);
}

public interface IProviderMetadataReader
{
    Task<IReadOnlyList<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetTablesAsync(string connectionString, string schema, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(string connectionString, string schema, string table, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schema, string table, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional provider metadata contract for DML result-row semantics whose validity depends on
/// provider-native trigger state. Implementations must fail closed when trigger metadata cannot be
/// proven complete; returning false is an assurance that no enabled trigger exists for the exact
/// resolved table and DML operation.
/// </summary>
public interface IProviderDmlResultRowMetadataReader
{
    Task<bool> HasEnabledDmlTriggerAsync(
        string connectionString,
        string schema,
        string table,
        DmlOperation operation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional additive result-row assurance contract over an already-open connection.
/// </summary>
public interface IProviderConnectionDmlResultRowMetadataReader
{
    Task<bool> HasEnabledDmlTriggerAsync(
        DbConnection connection,
        string schema,
        string table,
        DmlOperation operation,
        CancellationToken cancellationToken = default);
}

public interface IProviderErrorMapper
{
    Exception Map(Exception exception, string operation);
}

public interface ISqlProvider
{
    SqlAgentToolType Type { get; }
    IDbConnectionFactory Connections { get; }
    IProviderMetadataReader Metadata { get; }
    IProviderErrorMapper Errors { get; }
}

public interface ISqlProviderFactory
{
    ISqlProvider GetProvider(SqlAgentToolType type);
    IReadOnlyCollection<SqlAgentToolType> GetSupportedProviderTypes();
}
