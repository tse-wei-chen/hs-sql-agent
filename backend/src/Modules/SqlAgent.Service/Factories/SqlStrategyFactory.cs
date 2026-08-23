using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Transitional registration implementation. Provider resolution is now direct: each remaining
/// provider strategy shell implements ISqlProvider itself, so no strategy-to-provider adapter is
/// involved at runtime. Connection-string construction stays separate from the Core provider
/// contract until the historical provider class names are retired.
/// </summary>
public class SqlStrategyFactory : ISqlProviderFactory, ISqlConnectionStringFactory
{
    private readonly IReadOnlyDictionary<SqlAgentToolType, ISqlStrategy> _registrations;
    private readonly IReadOnlyDictionary<SqlAgentToolType, ISqlProvider> _providers;

    public SqlStrategyFactory(IEnumerable<ISqlStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        var registrations = new Dictionary<SqlAgentToolType, ISqlStrategy>();
        var providers = new Dictionary<SqlAgentToolType, ISqlProvider>();
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            if (!registrations.TryAdd(strategy.DbType, strategy))
            {
                throw new InvalidOperationException(
                    $"Duplicate strategy registration for database type: {strategy.DbType}");
            }

            if (strategy is not ISqlProvider provider)
            {
                throw new InvalidOperationException(
                    $"SQL provider registration {strategy.GetType().FullName} for {strategy.DbType} does not implement {nameof(ISqlProvider)}.");
            }

            providers.Add(strategy.DbType, provider);
        }

        _registrations = registrations;
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

    public string BuildConnectionString(
        SqlAgentToolType provider,
        BuildDbConnectionModelBase model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ResolveRegistration(provider).BuildConnectionString(model);
    }

    private ISqlStrategy ResolveRegistration(SqlAgentToolType dbType)
    {
        if (_registrations.TryGetValue(dbType, out var strategy))
            return strategy;

        throw new ArgumentOutOfRangeException(
            nameof(dbType),
            dbType,
            $"No SQL provider found for database type: {dbType}");
    }
}
