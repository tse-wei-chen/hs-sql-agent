using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Server-side strangler boundary for the typed DML pipeline. The MCP layer supplies the parsed
/// definition, current security policy and table authorization; this service owns provider
/// adaptation, immutable plan construction, preview and commit revalidation.
/// </summary>
public sealed class TypedDmlRuntime(
    TimeProvider? timeProvider = null,
    IDmlApprovalChallengeStore? challengeStore = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IDmlApprovalChallengeStore _challengeStore =
        challengeStore ?? new InMemoryDmlApprovalChallengeStore(timeProvider);

    public async Task<TypedDmlApprovalSession> PreviewAsync(
        ISqlStrategy strategy,
        string connectionString,
        DmlDefinition definition,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (definition.Operation is not (DmlOperation.Update or DmlOperation.Delete))
        {
            throw new NotSupportedException(
                "The typed DML runtime currently supports UPDATE and DELETE only. INSERT remains fail-closed until its production approval semantics are defined.");
        }

        var provider = new LegacySqlProviderAdapter(strategy);
        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, allowedTables),
            allowedTables);
        var compilationPolicy = new DmlCompilationPolicy(
            policy.RequireWhereForUpdate,
            policy.RequireWhereForDelete,
            policy.AllowFullTableUpdate,
            policy.AllowFullTableDelete);

        var plan = await new DmlPlanFactory(provider.Metadata).CreateAsync(
            connectionString,
            definition,
            provider.Type,
            provider.Type,
            validationContext,
            compilationPolicy,
            DmlRowIdentityAssurance.Strict,
            policy.DmlMaxAffectedRows,
            cancellationToken: cancellationToken);

        var coordinator = new DmlCoordinator(
            provider.Connections,
            _timeProvider,
            _challengeStore);
        var preview = await coordinator.PreviewAsync(
            connectionString,
            plan,
            cancellationToken);

        return new TypedDmlApprovalSession(plan, preview);
    }

    public async Task<DmlCommitResult> CommitAsync(
        ISqlStrategy strategy,
        string connectionString,
        TypedDmlApprovalSession session,
        SecurityPolicyModel currentPolicy,
        IReadOnlySet<string>? currentAllowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPolicy);
        var currentPolicyVersion = ComputePolicyVersion(currentPolicy, currentAllowedTables);
        if (!string.Equals(
                currentPolicyVersion,
                session.Plan.PolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DML security policy or table authorization changed after preview; request a new preview before committing.");
        }

        return await CommitCoreAsync(
            strategy,
            connectionString,
            session,
            cancellationToken);
    }

    private async Task<DmlCommitResult> CommitCoreAsync(
        ISqlStrategy strategy,
        string connectionString,
        TypedDmlApprovalSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var provider = new LegacySqlProviderAdapter(strategy);
        if (provider.Type != session.Plan.MutationCommand.TargetProvider)
        {
            throw new InvalidOperationException(
                "DML provider changed between preview and commit.");
        }

        var coordinator = new DmlCoordinator(
            provider.Connections,
            _timeProvider,
            _challengeStore);
        return await coordinator.CommitAsync(
            connectionString,
            session.Plan,
            session.Preview.Challenge,
            cancellationToken);
    }

    internal static string ComputePolicyVersion(
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var tables = allowedTables is null
            ? string.Empty
            : string.Join(',', allowedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var material =
            $"requireUpdateWhere={policy.RequireWhereForUpdate};" +
            $"requireDeleteWhere={policy.RequireWhereForDelete};" +
            $"allowFullUpdate={policy.AllowFullTableUpdate};" +
            $"allowFullDelete={policy.AllowFullTableDelete};" +
            $"maxAffected={policy.DmlMaxAffectedRows};" +
            $"updatedTicks={policy.UpdatedAt?.ToUniversalTime().Ticks ?? 0L};" +
            $"tables={tables}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

public sealed record TypedDmlApprovalSession(
    ValidatedDmlPlan Plan,
    DmlPreview Preview);
