using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Providers;

public sealed record DatabaseColumnMetadata(
    string Schema,
    string Table,
    string Name,
    string Type,
    bool IsPrimaryKey,
    int? PrimaryKeyOrdinal = null);

/// <summary>
/// One provider-native uniqueness rule that can affect INSERT conflict behavior. The inventory keeps
/// richer or currently unsupported shapes instead of filtering them out so callers can distinguish
/// "no other unique key exists" from "another unique key exists but Core cannot target it".
/// </summary>
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
    Task<IReadOnlyList<string>> GetSchemasAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetTablesAsync(
        string connectionString,
        string schema,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(
        string connectionString,
        string schema,
        string table,
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
    IProviderLowerer Lowerer { get; }
    IProviderMetadataReader Metadata { get; }
    IProviderErrorMapper Errors { get; }
}

/// <summary>
/// Resolves the complete provider runtime boundary by database type. Core/typed runtimes depend on
/// this provider abstraction rather than legacy strategies or provider-specific service locators.
/// </summary>
public interface ISqlProviderFactory
{
    ISqlProvider GetProvider(SqlAgentToolType type);
    IReadOnlyCollection<SqlAgentToolType> GetSupportedProviderTypes();
}
