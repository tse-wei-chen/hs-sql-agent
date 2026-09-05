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
    IDurableDmlApprovalLifecycle? durableLifecycle = null)
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
                0, null, false);

        var previewPolicy = securityPolicyRuntimeState.GetCurrent();
        var previewAllowedTables = resolveAllowedTables();
        TypedDmlTransactionApprovalSession session;
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
                return new TypedDmlExecutionTiming("Server busy: maximum concurrent SQL operations reached.", 0, null, false);
            session = await runtime.PreviewTransactionAsync(
                provider,
                connectionString,
                parsedBatch,
                previewPolicy,
                previewAllowedTables,
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
                approvalRequest.RequestId);
        }

        if (approvalResult.Decision != DmlApprovalDecision.Approved)
            return new TypedDmlExecutionTiming(
                approvalResult.Reason ?? "DML transaction execution cancelled by approval provider.",
                approvalWaitDurationMs,
                session.Challenge.AffectedRows,
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
                session.Challenge.AffectedRows,
                false);

        var commit = await runtime.CommitTransactionAsync(
            provider,
            connectionString,
            session,
            currentPolicy,
            currentAllowedTables,
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
            approvalRequest.RequestId);
    }
}
