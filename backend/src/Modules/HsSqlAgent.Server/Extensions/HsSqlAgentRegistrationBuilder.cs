using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Extensions;

/// <summary>
/// Composes HsSqlAgent server capabilities without forcing every integration concern onto the host.
/// </summary>
public sealed class HsSqlAgentRegistrationBuilder
{
    private readonly HashSet<string> _registeredFeatures = new(StringComparer.Ordinal);

    internal HsSqlAgentRegistrationBuilder(IServiceCollection services, HsSqlAgentServiceOptions options)
    {
        Services = services;
        Options = options;
    }

    public IServiceCollection Services { get; }

    public HsSqlAgentServiceOptions Options { get; }

    internal bool IsRegistered(string feature) => _registeredFeatures.Contains(feature);

    internal bool TryRegister(string feature)
    {
        if (!_registeredFeatures.Add(feature)) return false;
        Services.AddSingleton(new HsSqlAgentRegisteredFeature(feature));
        return true;
    }
}

internal sealed record HsSqlAgentRegisteredFeature(string Name);
