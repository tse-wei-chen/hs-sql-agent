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
                0, null, false);

        var previewPolicy = securityPolicyRuntimeState.GetCurrent();
        var previewAllowedTables = resolveAllowedTables();
        TypedDmlApprovalSession session;
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
                return new TypedDmlExecutionTiming("Server busy: maximum concurrent SQL operations reached.", 0, null, false);
            session = await runtime.PreviewAsync(
                provider, connectionString, parsedMutation, previewPolicy, previewAllowedTables, approvalContext, cancellationToken);
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
                approvalRequest.RequestId);
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
                "Server busy: maximum concurrent SQL operations reached.", approvalWaitDurationMs, session.Preview.AffectedRows, false);
        var commit = await runtime.CommitAsync(
            provider, connectionString, session, currentPolicy, currentAllowedTables, approvalContext, cancellationToken);
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
            approvalRequest.RequestId);
    }
}

internal readonly record struct TypedDmlExecutionTiming(
    string Result,
    long ApprovalWaitDurationMs,
    int? AffectedRows,
    bool Committed,
    DmlApprovalDecision? ApprovalDecision = null,
    string? ApprovalRequestId = null);
