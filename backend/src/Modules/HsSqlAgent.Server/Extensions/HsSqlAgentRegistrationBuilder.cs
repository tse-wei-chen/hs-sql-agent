using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Extensions;

/// <summary>
/// Composes HsSqlAgent server capabilities without forcing every integration concern onto the host.
/// </summary>
public sealed class HsSqlAgentRegistrationBuilder
{
    private readonly HashSet<string> _registeredFeatures = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _capabilityOptions = [];

    internal HsSqlAgentRegistrationBuilder(IServiceCollection services, HsSqlAgentServiceOptions? legacyOptions = null)
    {
        Services = services;
        LegacyOptions = legacyOptions;
    }

    public IServiceCollection Services { get; }

    /// <summary>
    /// Legacy aggregate options, populated only when AddHsSqlAgentCore(...) is called with the old aggregate shape.
    /// New modular registrations do not allocate this object.
    /// </summary>
    public HsSqlAgentServiceOptions? LegacyOptions { get; }

    internal bool IsRegistered(string feature) => _registeredFeatures.Contains(feature);

    internal bool TryRegister(string feature)
    {
        if (!_registeredFeatures.Add(feature)) return false;
        Services.AddSingleton(new HsSqlAgentRegisteredFeature(feature));
        return true;
    }

    internal TOptions GetOrCreateOptions<TOptions>(Func<TOptions> factory)
        where TOptions : class
    {
        if (_capabilityOptions.TryGetValue(typeof(TOptions), out var existing))
            return (TOptions)existing;

        var options = factory();
        _capabilityOptions.Add(typeof(TOptions), options);
        Services.AddSingleton(options);
        return options;
    }

    internal TOptions GetRequiredOptions<TOptions>()
        where TOptions : class
        => _capabilityOptions.TryGetValue(typeof(TOptions), out var options)
            ? (TOptions)options
            : throw new InvalidOperationException($"HsSqlAgent capability options {typeof(TOptions).Name} have not been registered.");

    internal void ThrowIfAlreadyConfigured(string feature, Delegate? configure)
    {
        if (configure is not null && IsRegistered(feature))
        {
            throw new InvalidOperationException(
                $"HsSqlAgent capability '{feature}' is already registered. Configure it on the first AddHsSqlAgent* call.");
        }
    }
}

internal sealed record HsSqlAgentRegisteredFeature(string Name);
