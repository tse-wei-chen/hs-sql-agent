using SqlAgent.Service.Core.Execution;
using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Tools;

internal sealed class TypedDmlApprovalFlow(
    TypedDmlRuntime runtime,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter concurrencyLimiter,
    Func<IReadOnlySet<string>?> resolveAllowedTables)
{
    public async Task<TypedDmlExecutionTiming> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedStatement parsedMutation,
        DmlApprovalExecutionContext approvalContext,
        IDmlApprovalProvider? approvalProvider,
        string approvalTitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(parsedMutation);
        if (approvalProvider is null)
            return new TypedDmlExecutionTiming(
                "Error: This MCP client does not support the interactive confirmation required for DML execution. Configure IDmlApprovalProvider to use another approval system.",
                0,
                null,
                false,
                ErrorCode: "approval.unavailable",
                ErrorStage: "Approval");

        var previewPolicy = securityPolicyRuntimeState.GetCurrent();
        var previewAllowedTables = resolveAllowedTables();
        TypedDmlApprovalSession session;
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
            // Preview and approval evidence must never survive a revoked
            // distributed admission. This scope ends before human approval.
            using var leaseCancellation = new SqlExecutionLeaseCancellation(lease, cancellationToken);
            leaseCancellation.Token.ThrowIfCancellationRequested();
            session = await runtime.PreviewAsync(
                provider, connectionString, parsedMutation, previewPolicy, previewAllowedTables,
                approvalContext, leaseCancellation.Token);
            leaseCancellation.Token.ThrowIfCancellationRequested();
        }

        var approvalRequest = DmlApprovalRequestFactory.Create(approvalTitle, approvalContext, session);
        var approvalStopwatch = Stopwatch.StartNew();
        DmlApprovalResult approvalResult;
        try { approvalResult = await approvalProvider.RequestApprovalAsync(approvalRequest, cancellationToken); }
        finally { approvalStopwatch.Stop(); }

        var approvalWaitDurationMs = approvalStopwatch.ElapsedMilliseconds;
        DmlApprovalRequestFactory.EnsureBoundResult(approvalRequest, approvalResult);
        if (approvalResult.Decision == DmlApprovalDecision.Pending)
            return new TypedDmlExecutionTiming(
                "DML approval is pending external review. No database changes were committed.",
                approvalWaitDurationMs,
                session.Preview.AffectedRows,
                false,
                DmlApprovalDecision.Pending,
                approvalRequest.RequestId,
                approvalResult.ExternalReference);
        if (approvalResult.Decision != DmlApprovalDecision.Approved)
            return new TypedDmlExecutionTiming(
                approvalResult.Reason ?? "DML execution cancelled by approval provider.",
                approvalWaitDurationMs,
                session.Preview.AffectedRows,
                false,
                DmlApprovalDecision.Rejected,
                approvalRequest.RequestId);

        var currentPolicy = securityPolicyRuntimeState.GetCurrent();
        var currentAllowedTables = resolveAllowedTables();
        await using var commitLease = await concurrencyLimiter.TryAcquireAsync(cancellationToken);
        if (commitLease is null)
            return new TypedDmlExecutionTiming(
                "Server busy: maximum concurrent SQL operations reached.",
                approvalWaitDurationMs,
                session.Preview.AffectedRows,
                false,
                DmlApprovalDecision.Approved,
                approvalRequest.RequestId,
                ErrorCode: "server.busy",
                ErrorStage: "Execution",
                Retryable: true);
        // Redis admission may expire between approval and the first native
        // COMMIT operation. Reject before consuming the challenge or touching
        // the target DB; a fresh human approval is required.
        if (SqlDmlCommitLeaseAdmission.IsRevoked(commitLease))
            return new TypedDmlExecutionTiming(
                SqlDmlCommitLeaseAdmission.RefusalMessage,
                approvalWaitDurationMs,
                session.Preview.AffectedRows,
                false,
                DmlApprovalDecision.Approved,
                approvalRequest.RequestId,
                ErrorCode: SqlDmlCommitLeaseAdmission.ErrorCode,
                ErrorStage: "Admission");
        DmlCommitResult commit;
        try
        {
            commit = await runtime.CommitAsync(
                provider, connectionString, session, currentPolicy, currentAllowedTables, approvalContext, cancellationToken);
        }
        catch (DmlCommitOutcomeUnknownException ex)
        {
            // Keep approval correlation for reconciliation. The original
            // challenge has already been consumed and cannot be replayed.
            return new TypedDmlExecutionTiming(
                ex.Message,
                approvalWaitDurationMs,
                null,
                false,
                DmlApprovalDecision.Approved,
                approvalRequest.RequestId,
                ErrorCode: "dml.commit_outcome_unknown",
                ErrorStage: "Commit",
                Retryable: false);
        }
        catch (DmlRollbackOutcomeUnknownException ex)
        {
            // A failed rollback does not prove the transaction was undone.
            // The approval was consumed and must never be replayed.
            return new TypedDmlExecutionTiming(
                ex.Message,
                approvalWaitDurationMs,
                null,
                false,
                DmlApprovalDecision.Approved,
                approvalRequest.RequestId,
                ErrorCode: "dml.rollback_outcome_unknown",
                ErrorStage: "Rollback",
                Retryable: false);
        }
        var result = commit.Committed
            ? $"Success | affectedRows={commit.AffectedRows} | {commit.Message}"
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
}

internal readonly record struct TypedDmlExecutionTiming(
    string Result,
    long ApprovalWaitDurationMs,
    int? AffectedRows,
    bool Committed,
    DmlApprovalDecision? ApprovalDecision = null,
    string? ApprovalRequestId = null,
    string? ApprovalExternalReference = null,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? ReturnedRows = null,
    string? ErrorCode = null,
    string? ErrorStage = null,
    bool Retryable = false);
