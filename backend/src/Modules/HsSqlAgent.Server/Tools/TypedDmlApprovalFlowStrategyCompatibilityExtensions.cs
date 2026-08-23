using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;

namespace HsSqlAgent.Server.Tools;

/// <summary>
/// Temporary MCP call-site bridge while server entry points migrate from strategies to providers.
/// The approval flow and typed DML runtime remain provider-native.
/// </summary>
internal static class TypedDmlApprovalFlowStrategyCompatibilityExtensions
{
    public static Task<TypedDmlExecutionTiming> ExecuteAsync(
        this TypedDmlApprovalFlow flow,
        ISqlStrategy strategy,
        string connectionString,
        DmlDefinition definition,
        IDmlApprovalClient? approvalClient,
        string approvalTitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.ExecuteAsync(
            StrategyBackedSqlProviderFactory.CreateProvider(strategy),
            connectionString,
            definition,
            approvalClient,
            approvalTitle,
            cancellationToken);
    }
}
