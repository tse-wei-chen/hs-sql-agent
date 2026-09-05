using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Server-side typed DML boundary. Single-statement and multi-statement entry points share the same
/// immutable per-statement plans. A multi-statement approval additionally binds statement order and
/// preview evidence into a one-time transaction challenge and commits all mutations atomically.
/// </summary>
public sealed class TypedDmlRuntime(
    TimeProvider? timeProvider = null,
    IDmlApprovalChallengeStore? challengeStore = null,
    IDmlPreviewTransactionFactory? previewTransactionFactory = null,
    ISqlCompileEvidenceObserver? compileEvidenceObserver = null)
{
    private const int MaxStatementsPerTransaction = 16;
    private static readonly IDmlPreviewTransactionFactory DriverNeutralPreviewTransactions =
        new ProviderDmlPreviewTransactionFactory();

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IDmlApprovalChallengeStore _challengeStore =
        challengeStore ?? new InMemoryDmlApprovalChallengeStore(timeProvider);
    private readonly IDmlPreviewTransactionFactory? _previewTransactionFactory = previewTransactionFactory;
    private readonly ISqlCompileEvidenceObserver? _compileEvidenceObserver = compileEvidenceObserver;

    public async Task<ParsedStatement> ParseDmlWithVerifiedRuntimeProfileAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var verificationConnection = provider.Connections.Create(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        var verifiedProfile = RuntimeServerProfileVerifier.Capture(provider.Type, verificationConnection);

        return sourceDialect == provider.Type
            ? CoreSqlTextParser.ParseDml(sql, sourceDialect, verifiedProfile.TargetProfile)
            : CoreSqlTextParser.ParseDml(sql, sourceDialect);
    }

    public async Task<ParsedDmlBatch> ParseDmlBatchWithVerifiedRuntimeProfileAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var verificationConnection = provider.Connections.Create(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        var verifiedProfile = RuntimeServerProfileVerifier.Capture(provider.Type, verificationConnection);

        return sourceDialect == provider.Type
            ? CoreDmlBatchTextParser.ParseDmlBatch(sql, sourceDialect, verifiedProfile.TargetProfile)
            : CoreDmlBatchTextParser.ParseDmlBatch(sql, sourceDialect);
    }

    public async Task<TypedDmlApprovalSession> PreviewAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedStatement parsedMutation,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        DmlApprovalExecutionContext approvalContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(parsedMutation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        EnsureSupportedStatement(parsedMutation.Statement);
        ValidateApprovalContextFields(approvalContext);
        if (provider.Type != approvalContext.Provider)
            throw new InvalidOperationException("DML approval provider does not match the current execution context.");
        var approvalContextFingerprint = ComputeApprovalContextFingerprint(approvalContext);

        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, allowedTables),
            allowedTables);
        var compilationPolicy = new DmlCompilationPolicy(
            policy.RequireWhereForUpdate,
            policy.RequireWhereForDelete,
            policy.AllowFullTableUpdate,
            policy.AllowFullTableDelete);

        VerifiedRuntimeServerProfile verifiedProfile;
        ValidatedDmlPlan plan;
        await using (var verificationConnection = provider.Connections.Create(connectionString))
        {
            await verificationConnection.OpenAsync(cancellationToken);
            verifiedProfile = RuntimeServerProfileVerifier.Capture(provider.Type, verificationConnection);

            try
            {
                plan = await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
                    verificationConnection,
                    connectionString,
                    parsedMutation,
                    provider.Type,
                    validationContext,
                    compilationPolicy,
                    DmlRowIdentityAssurance.Strict,
                    policy.DmlMaxAffectedRows,
                    cancellationToken: cancellationToken,
                    targetProfile: verifiedProfile.TargetProfile);
                _compileEvidenceObserver?.Observe(plan.MutationCommand.CompileEvidence);
            }
            catch (Exception exception)
            {
                _compileEvidenceObserver?.Observe(exception);
                throw;
            }
        }

        var coordinator = new DmlCoordinator(
            provider.Connections,
            _timeProvider,
            _challengeStore,
            previewTransactionFactory: ResolvePreviewTransactions(provider));
        var preview = await coordinator.PreviewAsync(
            connectionString,
            plan,
            approvalContextFingerprint,
            cancellationToken,
            verifiedProfile.ServerVersionIdentity);

        return new TypedDmlApprovalSession(plan, preview, verifiedProfile.ServerVersionIdentity);
    }

    public async Task<TypedDmlTransactionApprovalSession> PreviewTransactionAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedDmlBatch parsedBatch,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        DmlApprovalExecutionContext approvalContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsedBatch);
        if (parsedBatch.Count == 0)
            throw new InvalidOperationException("DML transaction must contain at least one statement.");
        if (parsedBatch.Count > MaxStatementsPerTransaction)
            throw new UnauthorizedAccessException(
                $"DML transaction contains {parsedBatch.Count} statements; maximum is {MaxStatementsPerTransaction}.");

        var builder = ImmutableArray.CreateBuilder<TypedDmlApprovalSession>(parsedBatch.Count);
        try
        {
            foreach (var statement in parsedBatch.Statements)
            {
                EnsureSupportedStatement(statement.Statement);
                builder.Add(await PreviewAsync(
                    provider,
                    connectionString,
                    statement,
                    policy,
                    allowedTables,
                    approvalContext,
                    cancellationToken));
            }
        }
        catch
        {
            await ConsumeChildChallengesAsync(builder, cancellationToken);
            throw;
        }

        var statements = builder.ToImmutable();
        var totalLong = statements.Sum(x => (long)x.Preview.AffectedRows);
        if (totalLong > int.MaxValue)
        {
            await ConsumeChildChallengesAsync(statements, cancellationToken);
            throw new UnauthorizedAccessException("DML transaction affected row count exceeds the supported integer range.");
        }
        var total = (int)totalLong;
        if (policy.DmlMaxAffectedRows > 0 && total > policy.DmlMaxAffectedRows)
        {
            await ConsumeChildChallengesAsync(statements, cancellationToken);
            throw new UnauthorizedAccessException(
                $"Security policy denied DML transaction: affectedRows={total} exceeds maximum {policy.DmlMaxAffectedRows}.");
        }

        var policyVersion = statements[0].Plan.PolicyVersion;
        if (statements.Any(x => !string.Equals(x.Plan.PolicyVersion, policyVersion, StringComparison.Ordinal)))
        {
            await ConsumeChildChallengesAsync(statements, cancellationToken);
            throw new InvalidOperationException("DML transaction statements were compiled under different security policy versions.");
        }

        var serverVersion = statements[0].VerifiedServerVersionIdentity;
        if (string.IsNullOrWhiteSpace(serverVersion)
            || statements.Any(x => !string.Equals(x.VerifiedServerVersionIdentity, serverVersion, StringComparison.Ordinal)))
        {
            await ConsumeChildChallengesAsync(statements, cancellationToken);
            throw new InvalidOperationException("DML transaction statements were previewed against different runtime server versions.");
        }

        var planFingerprint = ComputeTransactionPlanFingerprint(statements);
        var evidenceFingerprint = ComputeTransactionEvidenceFingerprint(statements);
        var approvalContextFingerprint = ComputeApprovalContextFingerprint(approvalContext);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = statements.Min(x => x.Preview.Challenge.ExpiresAt);
        if (expiresAt <= now)
        {
            await ConsumeChildChallengesAsync(statements, cancellationToken);
            throw new InvalidOperationException("DML transaction approval expired during preview.");
        }

        await ConsumeChildChallengesRequiredAsync(statements, cancellationToken);
        var challenge = new DmlApprovalChallenge(
            planFingerprint,
            evidenceFingerprint,
            total,
            policyVersion,
            approvalContextFingerprint,
            now,
            expiresAt,
            Guid.NewGuid().ToString("N"));
        await _challengeStore.RegisterAsync(challenge, cancellationToken);
        return new TypedDmlTransactionApprovalSession(statements, challenge);
    }

    public async Task<DmlCommitResult> CommitAsync(
        ISqlProvider provider,
        string connectionString,
        TypedDmlApprovalSession session,
        SecurityPolicyModel currentPolicy,
        IReadOnlySet<string>? currentAllowedTables,
        DmlApprovalExecutionContext currentApprovalContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(currentPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var currentPolicyVersion = ComputePolicyVersion(currentPolicy, currentAllowedTables);
        if (!string.Equals(currentPolicyVersion, session.Plan.PolicyVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DML security policy or table authorization changed after preview; request a new preview before committing.");

        ValidateApprovalContextFields(currentApprovalContext);
        var currentApprovalContextFingerprint = ComputeApprovalContextFingerprint(currentApprovalContext);
        if (!string.Equals(
                currentApprovalContextFingerprint,
                session.Preview.Challenge.ApprovalContextFingerprint,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DML approval execution context changed after preview; request a new preview before committing.");

        if (currentApprovalContext.Provider != session.Plan.MutationCommand.TargetProvider
            || provider.Type != session.Plan.MutationCommand.TargetProvider)
            throw new InvalidOperationException("DML provider changed between preview and commit.");

        if (session.VerifiedServerVersionIdentity is null)
            throw new InvalidOperationException(
                "DML approval session is missing verified runtime server-version identity; request a new preview before committing.");

        var coordinator = new DmlCoordinator(
            provider.Connections,
            _timeProvider,
            _challengeStore,
            previewTransactionFactory: ResolvePreviewTransactions(provider));
        return await coordinator.CommitAsync(
            connectionString,
            session.Plan,
            session.Preview.Challenge,
            currentApprovalContextFingerprint,
            cancellationToken,
            session.VerifiedServerVersionIdentity);
    }

    public async Task<DmlCommitResult> CommitTransactionAsync(
        ISqlProvider provider,
        string connectionString,
        TypedDmlTransactionApprovalSession session,
        SecurityPolicyModel currentPolicy,
        IReadOnlySet<string>? currentAllowedTables,
        DmlApprovalExecutionContext currentApprovalContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(currentPolicy);
        if (session.Statements.IsDefaultOrEmpty)
            throw new InvalidOperationException("DML transaction approval session is empty.");

        var currentPolicyVersion = ComputePolicyVersion(currentPolicy, currentAllowedTables);
        if (!string.Equals(currentPolicyVersion, session.Challenge.PolicyVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DML security policy or table authorization changed after transaction preview; request a new preview.");

        ValidateApprovalContextFields(currentApprovalContext);
        var contextFingerprint = ComputeApprovalContextFingerprint(currentApprovalContext);
        if (!string.Equals(contextFingerprint, session.Challenge.ApprovalContextFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DML approval execution context changed after transaction preview; request a new preview.");

        var now = _timeProvider.GetUtcNow();
        if (session.Challenge.ExpiresAt <= now || session.Challenge.IssuedAt > now || string.IsNullOrWhiteSpace(session.Challenge.Nonce))
            throw new InvalidOperationException("DML transaction approval challenge is expired or invalid.");

        var expectedPlanFingerprint = ComputeTransactionPlanFingerprint(session.Statements);
        var expectedEvidenceFingerprint = ComputeTransactionEvidenceFingerprint(session.Statements);
        var total = checked(session.Statements.Sum(x => x.Preview.AffectedRows));
        if (!string.Equals(expectedPlanFingerprint, session.Challenge.PlanFingerprint, StringComparison.Ordinal)
            || !string.Equals(expectedEvidenceFingerprint, session.Challenge.RowSetFingerprint, StringComparison.Ordinal)
            || total != session.Challenge.AffectedRows)
            throw new InvalidOperationException("DML transaction plan, order, or preview evidence changed after approval.");

        foreach (var statement in session.Statements)
        {
            if (provider.Type != statement.Plan.MutationCommand.TargetProvider
                || currentApprovalContext.Provider != statement.Plan.MutationCommand.TargetProvider)
                throw new InvalidOperationException("DML transaction provider changed between preview and commit.");
        }

        var serverVersion = session.Statements[0].VerifiedServerVersionIdentity;
        if (string.IsNullOrWhiteSpace(serverVersion)
            || session.Statements.Any(x => !string.Equals(x.VerifiedServerVersionIdentity, serverVersion, StringComparison.Ordinal)))
            throw new InvalidOperationException("DML transaction runtime server version changed within the approval session.");

        if (!await _challengeStore.TryConsumeAsync(session.Challenge, cancellationToken))
            throw new InvalidOperationException(
                "DML transaction approval challenge is unknown, modified, expired, or has already been consumed.");

        var coordinator = new DmlAtomicTransactionCoordinator(provider.Connections);
        return await coordinator.CommitAsync(
            connectionString,
            session.Statements.Select(x => x.Plan).ToArray(),
            session.Statements.Select(x => x.Preview).ToArray(),
            cancellationToken,
            serverVersion);
    }

    private async Task ConsumeChildChallengesRequiredAsync(
        IEnumerable<TypedDmlApprovalSession> statements,
        CancellationToken cancellationToken)
    {
        foreach (var statement in statements)
            if (!await _challengeStore.TryConsumeAsync(statement.Preview.Challenge, cancellationToken))
                throw new InvalidOperationException("Unable to consolidate one-time DML statement challenges into transaction approval.");
    }

    private async Task ConsumeChildChallengesAsync(
        IEnumerable<TypedDmlApprovalSession> statements,
        CancellationToken cancellationToken)
    {
        foreach (var statement in statements)
            await _challengeStore.TryConsumeAsync(statement.Preview.Challenge, cancellationToken);
    }

    private static string ComputeTransactionPlanFingerprint(IReadOnlyList<TypedDmlApprovalSession> statements)
    {
        var material = new StringBuilder("dml-transaction-plan-v1").Append('|').Append(statements.Count);
        for (var i = 0; i < statements.Count; i++)
            material.Append('|').Append(i).Append(':').Append(statements[i].Plan.PlanFingerprint);
        return ComputeHash(material.ToString());
    }

    private static string ComputeTransactionEvidenceFingerprint(IReadOnlyList<TypedDmlApprovalSession> statements)
    {
        var material = new StringBuilder("dml-transaction-evidence-v1").Append('|').Append(statements.Count);
        for (var i = 0; i < statements.Count; i++)
        {
            var preview = statements[i].Preview;
            material.Append('|').Append(i)
                .Append(':').Append(preview.AffectedRows)
                .Append(':').Append(preview.Challenge.RowSetFingerprint ?? "-")
                .Append(':').Append(statements[i].Plan.PlanFingerprint);
        }
        return ComputeHash(material.ToString());
    }

    private static string ComputeHash(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

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
            throw new NotSupportedException(
                "The typed DML approval runtime does not execute INSERT upsert/conflict clauses yet. A conflict can update or skip an existing row, so immutable INSERT payload approval is insufficient; existing-row impact must be previewed and revalidated first.");
        if (statement is InsertStatement)
            throw new NotSupportedException(
                "The typed DML runtime supports INSERT VALUES only. INSERT ... SELECT remains fail-closed until source-rowset approval semantics are defined.");
        throw new NotSupportedException(
            $"The typed DML runtime does not support statement '{statement.GetType().Name}'.");
    }

    internal static string ComputeApprovalContextFingerprint(DmlApprovalExecutionContext approvalContext)
    {
        ValidateApprovalContextFields(approvalContext);
        var material =
            "v1|" +
            FingerprintComponent(approvalContext.PrincipalIdentity) + "|" +
            FingerprintComponent(approvalContext.TargetIdentity) + "|" +
            FingerprintComponent(approvalContext.Provider.ToString()) + "|" +
            FingerprintComponent(approvalContext.DatabaseIdentity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static void ValidateApprovalContextFields(DmlApprovalExecutionContext approvalContext)
    {
        ArgumentNullException.ThrowIfNull(approvalContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalContext.PrincipalIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalContext.TargetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalContext.DatabaseIdentity);
    }

    private static string FingerprintComponent(string value)
    {
        value ??= string.Empty;
        return Encoding.UTF8.GetByteCount(value) + ":" + value;
    }

    internal static string ComputePolicyVersion(SecurityPolicyModel policy, IReadOnlySet<string>? allowedTables)
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
    DmlPreview Preview,
    string? VerifiedServerVersionIdentity = null);

public sealed record TypedDmlTransactionApprovalSession(
    ImmutableArray<TypedDmlApprovalSession> Statements,
    DmlApprovalChallenge Challenge);

public sealed record DmlApprovalExecutionContext(
    string PrincipalIdentity,
    string TargetIdentity,
    SqlAgentToolType Provider,
    string DatabaseIdentity);
