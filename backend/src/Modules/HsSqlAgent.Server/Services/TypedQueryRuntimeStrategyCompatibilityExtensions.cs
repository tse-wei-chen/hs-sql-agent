using Admin.Service.Models;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Temporary call-site bridge while MCP/server entry points migrate from ISqlStrategyFactory to
/// ISqlProviderFactory. TypedQueryRuntime itself is provider-native; new code must pass ISqlProvider.
/// </summary>
public static class TypedQueryRuntimeStrategyCompatibilityExtensions
{
    public static Task<QueryExecutionResult> ExecuteAsync(
        this ITypedQueryRuntime runtime,
        ISqlStrategy strategy,
        string connectionString,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.ExecuteAsync(
            StrategyBackedSqlProviderFactory.CreateProvider(strategy),
            connectionString,
            definition,
            sourceDialect,
            policy,
            allowedTables,
            cancellationToken);
    }

    public static CompiledSqlCommand Compile(
        this TypedQueryRuntime runtime,
        ISqlStrategy strategy,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Compile(
            StrategyBackedSqlProviderFactory.CreateProvider(strategy),
            definition,
            sourceDialect,
            policy,
            allowedTables);
    }
}
