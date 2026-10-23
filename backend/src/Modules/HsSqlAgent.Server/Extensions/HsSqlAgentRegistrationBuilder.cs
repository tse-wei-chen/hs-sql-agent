using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Extensions;

/// <summary>
/// Composes HsSqlAgent server capabilities without forcing every integration concern onto the host.
/// </summary>
public sealed class HsSqlAgentRegistrationBuilder
{
    private readonly HsSqlAgentRegistrationState _state;

    internal HsSqlAgentRegistrationBuilder(IServiceCollection services, HsSqlAgentRegistrationState state)
    {
        Services = services;
        _state = state;
    }

    public IServiceCollection Services { get; }

    /// <summary>
    /// Legacy aggregate options, populated only when AddHsSqlAgentCore(...) is called with the old aggregate shape.
    /// New modular registrations do not allocate this object.
    /// </summary>
    public HsSqlAgentServiceOptions? LegacyOptions => _state.LegacyOptions;

    internal bool IsRegistered(string feature) => _state.RegisteredFeatures.Contains(feature);

    internal bool TryRegister(string feature)
    {
        if (!_state.RegisteredFeatures.Add(feature)) return false;
        Services.AddSingleton(new HsSqlAgentRegisteredFeature(feature));
        return true;
    }

    internal TOptions GetOrCreateOptions<TOptions>(Func<TOptions> factory)
        where TOptions : class
    {
        if (_state.CapabilityOptions.TryGetValue(typeof(TOptions), out var existing))
            return (TOptions)existing;

        var options = factory();
        _state.CapabilityOptions.Add(typeof(TOptions), options);
        Services.AddSingleton(options);
        return options;
    }

    internal TOptions GetRequiredOptions<TOptions>()
        where TOptions : class
        => _state.CapabilityOptions.TryGetValue(typeof(TOptions), out var options)
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

/// <summary>
/// Registration is scoped to the host's IServiceCollection, not to a temporary
/// fluent builder. Independent NuGet modules can safely compose capabilities
/// without duplicating service descriptors or bypassing authorization modes.
/// </summary>
internal sealed class HsSqlAgentRegistrationState(HsSqlAgentServiceOptions? legacyOptions)
{
    internal HsSqlAgentServiceOptions? LegacyOptions { get; } = legacyOptions;
    internal HashSet<string> RegisteredFeatures { get; } = new(StringComparer.Ordinal);
    internal Dictionary<Type, object> CapabilityOptions { get; } = [];
}

internal sealed record HsSqlAgentRegisteredFeature(string Name);
