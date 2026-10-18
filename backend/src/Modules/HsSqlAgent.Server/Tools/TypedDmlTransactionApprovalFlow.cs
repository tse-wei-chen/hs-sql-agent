using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.SqlParsing;

namespace HsSqlAgent.Server.Tools;

/// <summary>
/// Approval-provider orchestration for one or more ordered DML statements. Pending decisions are
/// persisted by the durable lifecycle; immediate approvals retain the existing atomic commit path.
/// </summary>
internal sealed class TypedDmlTransactionApprovalFlow(
    TypedDmlRuntime runtime,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter concurrencyLimiter,
    Func<IReadOnlySet<string>?> resolveAllowedTables,
    IDurableDmlApprovalLifecycle? durableLifecycle = null,
    IMcpDmlCommitAuthorizationVerifier? commitAuthorizationVerifier = null,
    McpSqlExecutionScope? authenticatedScope = null,
    int? customToolId = null,
    int? customToolRevisionId = null)
{
    public async Task<TypedDmlExecutionTiming> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedDmlBatch parsedBatch,
        DmlApprovalExecutionContext approvalContext,
        IDmlApprovalProvider? approvalProvider,
        string approvalTitle,
        CancellationToken cancellationToken,
        DmlApprovalResumeContext? resumeContext = null)
    {
        ArgumentNullException.ThrowIfNull(parsedBatch);
        if (approvalProvider is null)
            return new TypedDmlExecutionTiming(
                "Error: This MCP client does not support the interactive confirmation required for DML execution. Configure IDmlApprovalProvider to use another approval system.",
                0,
                null,
                false,
                ErrorCode: "approval.unavailable",
                ErrorStage: "Approval");

        if (commitAuthorizationVerifier is null || authenticatedScope is null)
            throw new UnauthorizedAccessException(
                "DML approval requires a persistent MCP authorization verifier.");

        McpDmlCommitAuthorizationSnapshot previewAuthorization;
        SqlExecutionPolicyDecision previewDecision;
        TypedDmlTransactionApprovalSession session;
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
                return new TypedDmlExecutionTiming(
                    "Server busy: maximum concurrent SQL operations reached.",
                    0,
                    null,
                    false,
                    ErrorCode: "server.busy",
                    ErrorStage: "Execution",
                    Retryable: true);
            var contextTables = resolveAllowedTables();
            previewAuthorization = await commitAuthorizationVerifier.CaptureAsync(
                authenticatedScope, connectionString, customToolId, customToolRevisionId, cancellationToken);
            if (!SameTables(contextTables, previewAuthorization.AllowedTables))
                throw new UnauthorizedAccessException("MCP table permissions changed before DML preview.");
            previewDecision = SqlExecutionPolicyDecision.ForDml(
                approvalContext, securityPolicyRuntimeState.GetCurrent(),
                previewAuthorization.AllowedTables);
            session = await runtime.PreviewTransactionAsync(
                provider,
                connectionString,
                parsedBatch,
                previewDecision.PolicySnapshot,
                previewDecision.AllowedTables,
                approvalContext,
                cancellationToken);
        }

        var approvalRequest = DmlApprovalRequestFactory.Create(approvalTitle, approvalContext, session);
        var approvalStopwatch = Stopwatch.StartNew();
        DmlApprovalResult approvalResult;
        try { approvalResult = await approvalProvider.RequestApprovalAsync(approvalRequest, cancellationToken); }
        finally { approvalStopwatch.Stop(); }

        var approvalWaitDurationMs = approvalStopwatch.ElapsedMilliseconds;
        DmlApprovalRequestFactory.EnsureBoundResult(approvalRequest, approvalResult);
        if (approvalResult.Decision == DmlApprovalDecision.Pending)
        {
            if (durableLifecycle is null || resumeContext is null)
                throw new InvalidOperationException(
                    "The configured DML approval provider returned Pending, but durable approval lifecycle services are unavailable.");
            await durableLifecycle.PersistPendingAsync(
                approvalRequest,
                approvalResult,
                DmlApprovalRequestFactory.ComputeEvidenceFingerprint(session),
                resumeContext,
                connectionString,
                cancellationToken);
            var reference = string.IsNullOrWhiteSpace(approvalResult.ExternalReference)
                ? string.Empty
                : $" externalReference={approvalResult.ExternalReference}";
            return new TypedDmlExecutionTiming(
                $"DML transaction approval is pending external review. requestId={approvalRequest.RequestId}.{reference} No database changes were committed.",
                approvalWaitDurationMs,
                session.Challenge.AffectedRows,
                false,
                DmlApprovalDecision.Pending,
                approvalRequest.RequestId,
                approvalResult.ExternalReference);
        }

        if (approvalResult.Decision != DmlApprovalDecision.Approved)
            return new TypedDmlExecutionTiming(
                approvalResult.Reason ?? "DML transaction execution cancelled by approval provider.",
                approvalWaitDurationMs,
                session.Challenge.AffectedRows,
                false,
                DmlApprovalDecision.Rejected,
                approvalRequest.RequestId);

        await using var commitLease = await concurrencyLimiter.TryAcquireAsync(cancellationToken);
        if (commitLease is null)
            return new TypedDmlExecutionTiming(
                "Server busy: maximum concurrent SQL operations reached.",
                approvalWaitDurationMs,
                session.Challenge.AffectedRows,
                false,
                DmlApprovalDecision.Approved,
                approvalRequest.RequestId,
                ErrorCode: "server.busy",
                ErrorStage: "Execution",
                Retryable: true);

        // Human approval may outlive the MCP key: HttpContext.Items is NOT an
        // authority after the await. Read persisted permissions under commit lease.
        var currentContextTables = resolveAllowedTables();
        var currentAuthorization = await commitAuthorizationVerifier.CaptureAsync(
            authenticatedScope, connectionString, customToolId, customToolRevisionId, cancellationToken);
        previewAuthorization.EnsureUnchanged(currentAuthorization);
        if (!SameTables(currentContextTables, currentAuthorization.AllowedTables))
            throw new UnauthorizedAccessException("MCP table permissions changed during DML approval.");

        var currentDecision = SqlExecutionPolicyDecision.ForDml(
            approvalContext, securityPolicyRuntimeState.GetCurrent(),
            currentAuthorization.AllowedTables);
        previewDecision.EnsureSameDmlDecision(currentDecision);

        var commit = await runtime.CommitTransactionAsync(
            provider,
            connectionString,
            session,
            currentDecision.PolicySnapshot,
            currentDecision.AllowedTables,
            approvalContext,
            cancellationToken);
        var result = commit.Committed
            ? $"Success | statements={session.Statements.Length} | affectedRows={commit.AffectedRows} | {commit.Message}"
            : commit.Message;
        if (commit.Committed && !commit.ReturnedRows.IsDefaultOrEmpty)
            result += $" | returnedRows={JsonSerializer.Serialize(commit.ReturnedRows)}";
        return new TypedDmlExecutionTiming(
            result,
            approvalWaitDurationMs,
            commit.AffectedRows,
            commit.Committed,
            DmlApprovalDecision.Approved,
            approvalRequest.RequestId,
            ReturnedRows: commit.ReturnedRows,
            ErrorCode: commit.Committed ? null : "dml.commit_rejected",
            ErrorStage: commit.Committed ? null : "Commit");
    }

    private static bool SameTables(IReadOnlySet<string>? first, IReadOnlySet<string>? second) =>
        first is null ? second is null
        : second is not null && first.Count == second.Count && first.All(second.Contains);
}
