using System.Data.Common;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Strategies.Adapters;

/// <summary>
/// Transitional adapter that reuses provider connection/metadata implementation while compilation
/// and execution move out of BaseSqlStrategy. It can be deleted after native provider components
/// replace the strategy subclasses.
/// </summary>
public sealed class LegacySqlProviderAdapter : ISqlProvider
{
    public LegacySqlProviderAdapter(ISqlStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        Type = strategy.DbType;
        Connections = new StrategyConnectionFactory(strategy);
        Lowerer = new SqlKataProviderLowerer(strategy.DbType);
        Metadata = new StrategyMetadataReader(strategy);
        Errors = new PassThroughProviderErrorMapper();
    }

    public SqlAgentToolType Type { get; }
    public IDbConnectionFactory Connections { get; }
    public IProviderLowerer Lowerer { get; }
    public IProviderMetadataReader Metadata { get; }
    public IProviderErrorMapper Errors { get; }

    private sealed class StrategyConnectionFactory(ISqlStrategy strategy) : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) =>
            strategy.CreateConnection(connectionString);
    }

    private sealed class StrategyMetadataReader(ISqlStrategy strategy) : IProviderMetadataReader
    {
        public async Task<IReadOnlyList<string>> GetSchemasAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            await strategy.GetSchemasAsync(connectionString, cancellationToken);

        public async Task<IReadOnlyList<string>> GetTablesAsync(
            string connectionString,
            string schema,
            CancellationToken cancellationToken = default) =>
            await strategy.GetTablesAsync(connectionString, schema, cancellationToken);

        public async Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default)
        {
            var columns = await strategy.GetColumnsAsync(
                connectionString,
                schema,
                table,
                cancellationToken);
            return columns.Select(column => new DatabaseColumnMetadata(
                    schema,
                    table,
                    column.Name,
                    column.Type,
                    IsPrimaryKey: false))
                .ToArray();
        }
    }

    private sealed class PassThroughProviderErrorMapper : IProviderErrorMapper
    {
        public Exception Map(Exception exception, string operation) => exception;
    }
}
