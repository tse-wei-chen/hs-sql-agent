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
