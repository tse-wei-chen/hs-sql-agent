namespace HsSqlAgent.Provider.Abstractions;

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
