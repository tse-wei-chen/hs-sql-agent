using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Server-side typed DML boundary. The MCP layer supplies a parser-native Core statement, current
/// security policy and table authorization; this service owns immutable plan construction, preview
/// and commit revalidation against an explicit provider and never depends on transport DTOs or
/// legacy strategies.
/// </summary>
public sealed class TypedDmlRuntime(
    TimeProvider? timeProvider = null,
    IDmlApprovalChallengeStore? challengeStore = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IDmlApprovalChallengeStore _challengeStore =
        challengeStore ?? new InMemoryDmlApprovalChallengeStore(timeProvider);

    public async Task<TypedDmlApprovalSession> PreviewAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedStatement parsedMutation,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(parsedMutation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (parsedMutation.Statement is not (UpdateStatement or DeleteStatement))
        {
            throw new NotSupportedException(
                "The typed DML runtime currently supports UPDATE and DELETE only. INSERT remains fail-closed until its production approval semantics are defined.");
        }

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
            parsedMutation,
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
        ISqlProvider provider,
        string connectionString,
        TypedDmlApprovalSession session,
        SecurityPolicyModel currentPolicy,
        IReadOnlySet<string>? currentAllowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(currentPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var currentPolicyVersion = ComputePolicyVersion(currentPolicy, currentAllowedTables);
        if (!string.Equals(
                currentPolicyVersion,
                session.Plan.PolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DML security policy or table authorization changed after preview; request a new preview before committing.");
        }

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
