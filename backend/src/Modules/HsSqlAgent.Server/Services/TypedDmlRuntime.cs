using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Execution;

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
    IDmlPreviewTransactionFactory? previewTransactionFactory = null,
    ISqlCompileEvidenceObserver? compileEvidenceObserver = null)
{
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

        await using var verificationConnection =
            provider.Connections.Create(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        var verifiedProfile = RuntimeServerProfileVerifier.Capture(
            provider.Type,
            verificationConnection);

        return sourceDialect == provider.Type
            ? CoreSqlTextParser.ParseDml(
                sql,
                sourceDialect,
                verifiedProfile.TargetProfile)
            : CoreSqlTextParser.ParseDml(
                sql,
                sourceDialect);
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
        {
            throw new InvalidOperationException(
                "DML approval provider does not match the current execution context.");
        }
        var approvalContextFingerprint = ComputeApprovalContextFingerprint(approvalContext);

        await using var verificationConnection = provider.Connections.Create(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        var verifiedProfile = RuntimeServerProfileVerifier.Capture(
            provider.Type,
            verificationConnection);

        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, allowedTables),
            allowedTables);
        var compilationPolicy = new DmlCompilationPolicy(
            policy.RequireWhereForUpdate,
            policy.RequireWhereForDelete,
            policy.AllowFullTableUpdate,
            policy.AllowFullTableDelete);

        ValidatedDmlPlan plan;
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

        return new TypedDmlApprovalSession(
            plan,
            preview,
            verifiedProfile.ServerVersionIdentity);
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
        if (!string.Equals(
                currentPolicyVersion,
                session.Plan.PolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DML security policy or table authorization changed after preview; request a new preview before committing.");
        }

        ValidateApprovalContextFields(currentApprovalContext);
        var currentApprovalContextFingerprint =
            ComputeApprovalContextFingerprint(currentApprovalContext);
        if (!string.Equals(
                currentApprovalContextFingerprint,
                session.Preview.Challenge.ApprovalContextFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DML approval execution context changed after preview; request a new preview before committing.");
        }

        if (currentApprovalContext.Provider != session.Plan.MutationCommand.TargetProvider)
        {
            throw new InvalidOperationException(
                "DML approval target provider changed between preview and commit.");
        }

        if (provider.Type != session.Plan.MutationCommand.TargetProvider)
        {
            throw new InvalidOperationException(
                "DML provider changed between preview and commit.");
        }

        if (session.VerifiedServerVersionIdentity is null)
        {
            throw new InvalidOperationException(
                "DML approval session is missing verified runtime server-version identity; request a new preview before committing.");
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
            currentApprovalContextFingerprint,
            cancellationToken,
            session.VerifiedServerVersionIdentity);
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

    internal static string ComputeApprovalContextFingerprint(
        DmlApprovalExecutionContext approvalContext)
    {
        ValidateApprovalContextFields(approvalContext);
        var material =
            "v1|" +
            FingerprintComponent(approvalContext.PrincipalIdentity) + "|" +
            FingerprintComponent(approvalContext.TargetIdentity) + "|" +
            FingerprintComponent(approvalContext.Provider.ToString()) + "|" +
            FingerprintComponent(approvalContext.DatabaseIdentity);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static void ValidateApprovalContextFields(
        DmlApprovalExecutionContext approvalContext)
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
    DmlPreview Preview,
    string? VerifiedServerVersionIdentity = null);

public sealed record DmlApprovalExecutionContext(
    string PrincipalIdentity,
    string TargetIdentity,
    SqlAgentToolType Provider,
    string DatabaseIdentity);
