using System.Data.Common;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Strategies.Adapters;

/// <summary>
/// Transitional provider registry that isolates the remaining strategy connection/metadata
/// implementations behind the Core provider contract. No Core/typed runtime should consume
/// ISqlStrategy directly; this is the single strangler point until native provider components
/// replace the strategy shells.
/// </summary>
public sealed class StrategyBackedSqlProviderFactory : ISqlProviderFactory
{
    private readonly IReadOnlyDictionary<SqlAgentToolType, ISqlProvider> _providers;

    public StrategyBackedSqlProviderFactory(IEnumerable<ISqlStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        var providers = new Dictionary<SqlAgentToolType, ISqlProvider>();
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            if (!providers.TryAdd(strategy.DbType, CreateProvider(strategy)))
            {
                throw new InvalidOperationException(
                    $"Duplicate provider registration for database type: {strategy.DbType}");
            }
        }

        _providers = providers;
    }

    public ISqlProvider GetProvider(SqlAgentToolType type)
    {
        if (_providers.TryGetValue(type, out var provider))
            return provider;

        throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            $"No SQL provider found for database type: {type}");
    }

    public IReadOnlyCollection<SqlAgentToolType> GetSupportedProviderTypes() =>
        _providers.Keys.ToArray();

    /// <summary>
    /// Temporary single-strategy bridge for call sites that have not yet switched to
    /// ISqlProviderFactory. New production code should resolve providers through the factory.
    /// </summary>
    public static ISqlProvider CreateProvider(ISqlStrategy strategy)
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
