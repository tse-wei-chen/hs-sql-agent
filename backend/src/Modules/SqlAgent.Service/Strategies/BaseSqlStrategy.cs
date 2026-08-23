using System.Data.Common;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies.Adapters;

namespace SqlAgent.Service.Strategies;

/// <summary>
/// Provider implementation base retained under the historical strategy name while provider files
/// are migrated incrementally. SQL parsing, compilation, policy rewriting, lowering and execution
/// belong to the Core/typed runtime pipeline; this type now implements the provider runtime
/// capabilities directly instead of requiring a strategy-to-provider adapter.
/// </summary>
public abstract class BaseSqlStrategy : ISqlStrategy, ISqlProvider, IDbConnectionFactory, IProviderMetadataReader
{
    private IProviderLowerer? _lowerer;
    private IProviderErrorMapper? _errors;

    public abstract SqlAgentToolType DbType { get; }
    public SqlAgentToolType Type => DbType;

    public IDbConnectionFactory Connections => this;
    public IProviderLowerer Lowerer => _lowerer ??= new SqlKataProviderLowerer(DbType);
    public IProviderMetadataReader Metadata => this;
    public IProviderErrorMapper Errors => _errors ??= new ProviderExecutionErrorMapper(DbType);

    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);
    public abstract DbConnection CreateConnection(string? connectionString);

    public abstract Task<List<string>> GetSchemasAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    public abstract Task<List<string>> GetTablesAsync(
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken = default);

    public abstract Task<List<ColumnInfo>> GetColumnsAsync(
        string connectionString,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken = default);

    DbConnection IDbConnectionFactory.Create(string connectionString) =>
        CreateConnection(connectionString);

    async Task<IReadOnlyList<string>> IProviderMetadataReader.GetSchemasAsync(
        string connectionString,
        CancellationToken cancellationToken) =>
        await GetSchemasAsync(connectionString, cancellationToken);

    async Task<IReadOnlyList<string>> IProviderMetadataReader.GetTablesAsync(
        string connectionString,
        string schema,
        CancellationToken cancellationToken) =>
        await GetTablesAsync(connectionString, schema, cancellationToken);

    async Task<IReadOnlyList<DatabaseColumnMetadata>> IProviderMetadataReader.GetColumnsAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(
            connectionString,
            schema,
            table,
            cancellationToken);
        return columns
            .Select(column => new DatabaseColumnMetadata(
                schema,
                table,
                column.Name,
                column.Type,
                column.IsPrimaryKey,
                column.PrimaryKeyOrdinal))
            .ToArray();
    }
}
