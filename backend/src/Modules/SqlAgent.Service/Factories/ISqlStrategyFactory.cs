using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Transitional registration contract. Provider resolution is the supported runtime path; the
/// strategy-returning members remain only for management/MCP call sites that have not yet migrated.
/// </summary>
public interface ISqlStrategyFactory : ISqlProviderFactory
{
    [Obsolete("Use GetProvider(SqlAgentToolType). Strategy access is a transitional compatibility surface.")]
    ISqlStrategy GetStrategy(SqlAgentToolType dbType);

    [Obsolete("Use GetSupportedProviderTypes().")]
    IEnumerable<SqlAgentToolType> GetSupportedDatabaseTypes();
}
