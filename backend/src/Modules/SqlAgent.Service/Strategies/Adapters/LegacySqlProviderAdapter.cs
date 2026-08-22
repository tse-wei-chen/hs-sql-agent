using System.Data.Common;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Service.Strategies.Adapters;

/// <summary>
/// Transitional bridge that exposes the remaining strategy connection/metadata implementation as
/// Core provider collaborators. The returned provider itself is Core-owned; this adapter carries no
/// provider runtime state and can disappear once connection/metadata move to native components.
/// </summary>
public static class LegacySqlProviderAdapter
{
    public static ISqlProvider Adapt(ISqlStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return new SqlProvider(
            strategy.DbType,
            new StrategyConnectionFactory(strategy),
            new SqlKataProviderLowerer(strategy.DbType),
            new StrategyMetadataReader(strategy),
            new ProviderExecutionErrorMapper(strategy.DbType));
    }

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
                    column.IsPrimaryKey,
                    column.PrimaryKeyOrdinal))
                .ToArray();
        }
    }
}
