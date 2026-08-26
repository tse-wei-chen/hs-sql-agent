using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using HsSqlAgent.SqlCore.Core.Ast;
using SqlAgent.Service.Core.Execution;
using HsSqlAgent.SqlCore.Core.Pipeline;
using SqlAgent.Service.Core.Providers;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Server-side typed DML boundary. The MCP layer supplies a parser-native Core statement, current
/// security policy and table authorization; this service owns immutable plan construction, preview
/// and commit revalidation against an explicit provider and never depends on transport DTOs or
/// legacy strategies. Plain INSERT VALUES uses exact-payload approval; INSERT ... SELECT and INSERT
/// conflict/upsert remain fail-closed until their source/existing-row approval semantics are defined.
/// </summary>
public sealed class TypedDmlRuntime(
    TimeProvider? timeProvider = null,
    IDmlApprovalChallengeStore? challengeStore = null,
    IDmlPreviewTransactionFactory? previewTransactionFactory = null)
{
    private static readonly IDmlPreviewTransactionFactory DriverNeutralPreviewTransactions =
        new ProviderDmlPreviewTransactionFactory();

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IDmlApprovalChallengeStore _challengeStore =
        challengeStore ?? new InMemoryDmlApprovalChallengeStore(timeProvider);
    private readonly IDmlPreviewTransactionFactory? _previewTransactionFactory = previewTransactionFactory;

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

        EnsureSupportedStatement(parsedMutation.Statement);

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
            _challengeStore,
            previewTransactionFactory: ResolvePreviewTransactions(provider));
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
            _challengeStore,
            previewTransactionFactory: ResolvePreviewTransactions(provider));
        return await coordinator.CommitAsync(
            connectionString,
            session.Plan,
            session.Preview.Challenge,
            cancellationToken);
    }

    private IDmlPreviewTransactionFactory ResolvePreviewTransactions(ISqlProvider provider) =>
        _previewTransactionFactory
        ?? (provider as IProviderDmlPreviewTransactionSource)?.PreviewTransactions
        ?? DriverNeutralPreviewTransactions;

    internal static bool SupportsStatement(SqlStatement statement) =>
        statement is UpdateStatement
            or DeleteStatement
            or InsertStatement { Source: InsertValuesSource, Conflict: null };

    internal static void EnsureSupportedStatement(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (SupportsStatement(statement)) return;

        if (statement is InsertStatement { Conflict: not null })
        {
            throw new NotSupportedException(
                "The typed DML approval runtime does not execute INSERT upsert/conflict clauses yet. A conflict can update or skip an existing row, so immutable INSERT payload approval is insufficient; existing-row impact must be previewed and revalidated first.");
        }

        if (statement is InsertStatement)
        {
            throw new NotSupportedException(
                "The typed DML runtime supports INSERT VALUES only. INSERT ... SELECT remains fail-closed until source-rowset approval semantics are defined.");
        }

        throw new NotSupportedException(
            $"The typed DML runtime does not support statement '{statement.GetType().Name}'.");
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
