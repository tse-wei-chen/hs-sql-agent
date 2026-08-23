using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Transitional registration contract. Provider resolution and connection-string construction are
/// the supported paths; strategy-returning members remain only for MCP call sites not yet migrated.
/// </summary>
public interface ISqlStrategyFactory : ISqlProviderFactory, ISqlConnectionStringFactory
{
    [Obsolete("Use GetProvider(SqlAgentToolType). Strategy access is a transitional compatibility surface.")]
    ISqlStrategy GetStrategy(SqlAgentToolType dbType);

    [Obsolete("Use GetSupportedProviderTypes().")]
    IEnumerable<SqlAgentToolType> GetSupportedDatabaseTypes();
}
