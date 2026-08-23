using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Transitional registration implementation. ISqlStrategy now extends ISqlProvider, so every
/// remaining provider strategy is a Core provider by construction and no runtime provider cast or
/// parallel provider map is required. Connection-string construction stays separate from the Core
/// provider contract until the historical provider class names are retired.
/// </summary>
public class SqlStrategyFactory : ISqlProviderFactory, ISqlConnectionStringFactory
{
    private readonly IReadOnlyDictionary<SqlAgentToolType, ISqlStrategy> _providers;

    public SqlStrategyFactory(IEnumerable<ISqlStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        var providers = new Dictionary<SqlAgentToolType, ISqlStrategy>();
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            if (!providers.TryAdd(strategy.DbType, strategy))
            {
                throw new InvalidOperationException(
                    $"Duplicate strategy registration for database type: {strategy.DbType}");
            }
        }

        _providers = providers;
    }

    public ISqlProvider GetProvider(SqlAgentToolType type) =>
        ResolveProvider(type);

    public IReadOnlyCollection<SqlAgentToolType> GetSupportedProviderTypes() =>
        _providers.Keys.ToArray();

    public string BuildConnectionString(
        SqlAgentToolType provider,
        BuildDbConnectionModelBase model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ResolveProvider(provider).BuildConnectionString(model);
    }

    private ISqlStrategy ResolveProvider(SqlAgentToolType dbType)
    {
        if (_providers.TryGetValue(dbType, out var provider))
            return provider;

        throw new ArgumentOutOfRangeException(
            nameof(dbType),
            dbType,
            $"No SQL provider found for database type: {dbType}");
    }
}