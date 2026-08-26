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

public interface IProviderErrorMapper
{
    Exception Map(Exception exception, string operation);
}

public interface ISqlProvider
{
    SqlAgentToolType Type { get; }
    IDbConnectionFactory Connections { get; }
    IProviderLowerer Lowerer { get; }
    IProviderMetadataReader Metadata { get; }
    IProviderErrorMapper Errors { get; }
}

public interface ISqlProviderFactory
{
    ISqlProvider GetProvider(SqlAgentToolType type);
    IReadOnlyCollection<SqlAgentToolType> GetSupportedProviderTypes();
}
