using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Resolves the concrete provider runtime and keeps management-side connection-string construction
/// outside the ISqlProvider contract.
/// </summary>
public sealed class SqlProviderFactory : ISqlProviderFactory, ISqlConnectionStringFactory
{
    private readonly IReadOnlyDictionary<SqlAgentToolType, SqlProviderBase> _providers;

    public SqlProviderFactory(IEnumerable<SqlProviderBase> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var registrations = new Dictionary<SqlAgentToolType, SqlProviderBase>();
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!registrations.TryAdd(provider.Type, provider))
            {
                throw new InvalidOperationException(
                    $"Duplicate SQL provider registration for database type: {provider.Type}");
            }
        }

        _providers = registrations;
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

    private SqlProviderBase ResolveProvider(SqlAgentToolType type)
    {
        if (_providers.TryGetValue(type, out var provider))
            return provider;

        throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            $"No SQL provider found for database type: {type}");
    }
}
