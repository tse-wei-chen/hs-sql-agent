using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Transitional DI registration. Runtime callers resolve ISqlProvider; legacy strategy access is
/// retained only until the remaining management/MCP call sites are migrated.
/// </summary>
public class SqlStrategyFactory : ISqlStrategyFactory
{
    private readonly IReadOnlyDictionary<SqlAgentToolType, ISqlStrategy> _strategies;
    private readonly StrategyBackedSqlProviderFactory _providers;

    public SqlStrategyFactory(IEnumerable<ISqlStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        var materialized = strategies.ToArray();
        var map = new Dictionary<SqlAgentToolType, ISqlStrategy>();
        foreach (var strategy in materialized)
        {
            if (!map.TryAdd(strategy.DbType, strategy))
            {
                throw new InvalidOperationException(
                    $"Duplicate strategy registration for database type: {strategy.DbType}");
            }
        }

        _strategies = map;
        _providers = new StrategyBackedSqlProviderFactory(materialized);
    }

    public ISqlProvider GetProvider(SqlAgentToolType type) =>
        _providers.GetProvider(type);

    public IReadOnlyCollection<SqlAgentToolType> GetSupportedProviderTypes() =>
        _providers.GetSupportedProviderTypes();

    [Obsolete("Use GetProvider(SqlAgentToolType). Strategy access is a transitional compatibility surface.")]
    public ISqlStrategy GetStrategy(SqlAgentToolType dbType)
    {
        if (_strategies.TryGetValue(dbType, out var strategy))
            return strategy;

        throw new ArgumentOutOfRangeException(
            nameof(dbType),
            dbType,
            $"No strategy found for database type: {dbType}");
    }

    [Obsolete("Use GetSupportedProviderTypes().")]
    public IEnumerable<SqlAgentToolType> GetSupportedDatabaseTypes() =>
        GetSupportedProviderTypes();
}
