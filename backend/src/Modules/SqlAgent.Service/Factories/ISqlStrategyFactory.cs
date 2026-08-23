using SqlAgent.Service.Core.Providers;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Transitional DI alias combining provider resolution with management-side connection-string construction.
/// The contract no longer exposes legacy strategies; runtime callers must stay on provider-native surfaces.
/// </summary>
public interface ISqlStrategyFactory : ISqlProviderFactory, ISqlConnectionStringFactory
{
}
