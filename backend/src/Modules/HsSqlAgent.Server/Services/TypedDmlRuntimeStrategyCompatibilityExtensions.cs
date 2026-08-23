using Admin.Service.Models;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Temporary call-site bridge while server tests and MCP entry points migrate from strategies to
/// providers. TypedDmlRuntime itself remains provider-native.
/// </summary>
public static class TypedDmlRuntimeStrategyCompatibilityExtensions
{
    public static Task<TypedDmlApprovalSession> PreviewAsync(
        this TypedDmlRuntime runtime,
        ISqlStrategy strategy,
        string connectionString,
        DmlDefinition definition,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.PreviewAsync(
            StrategyBackedSqlProviderFactory.CreateProvider(strategy),
            connectionString,
            definition,
            policy,
            allowedTables,
            cancellationToken);
    }

    public static Task<DmlCommitResult> CommitAsync(
        this TypedDmlRuntime runtime,
        ISqlStrategy strategy,
        string connectionString,
        TypedDmlApprovalSession session,
        SecurityPolicyModel currentPolicy,
        IReadOnlySet<string>? currentAllowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.CommitAsync(
            StrategyBackedSqlProviderFactory.CreateProvider(strategy),
            connectionString,
            session,
            currentPolicy,
            currentAllowedTables,
            cancellationToken);
    }
}
