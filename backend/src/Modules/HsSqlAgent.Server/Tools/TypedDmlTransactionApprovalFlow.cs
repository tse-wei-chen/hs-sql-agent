using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.SqlParsing;

namespace HsSqlAgent.Server.Tools;

/// <summary>
/// Approval-provider orchestration for one or more ordered DML statements. One approval decision
/// covers the complete transaction; commit either persists every statement or the entire set rolls back.
/// </summary>
internal sealed class TypedDmlTransactionApprovalFlow(
    TypedDmlRuntime runtime,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter concurrencyLimiter,
    Func<IReadOnlySet<string>?> resolveAllowedTables)
{
    public async Task<TypedDmlExecutionTiming> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedDmlBatch parsedBatch,
        DmlApprovalExecutionContext approvalContext,
        IDmlApprovalProvider? approvalProvider,
        string approvalTitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parsedBatch);
        if (approvalProvider is null)
            return new TypedDmlExecutionTiming(
                "Error: This MCP client does not support the interactive confirmation required for DML execution. Configure IDmlApprovalProvider to use another approval system.",
                0,
                null,
                false);

        var previewPolicy = securityPolicyRuntimeState.GetCurrent();
        var previewAllowedTables = resolveAllowedTables();
        TypedDmlTransactionApprovalSession session;
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
                return new TypedDmlExecutionTiming(
                    "Server busy: maximum concurrent SQL operations reached.", 0, null, false);

            session = await runtime.PreviewTransactionAsync(
                provider,
                connectionString,
                parsedBatch,
                previewPolicy,
                previewAllowedTables,
                approvalContext,
                cancellationToken);
        }

        var approvalRequest = DmlApprovalRequestFactory.Create(
            approvalTitle,
            approvalContext,
            session);
        var approvalStopwatch = Stopwatch.StartNew();
        DmlApprovalResult approvalResult;
        try
        {
            approvalResult = await approvalProvider.RequestApprovalAsync(
                approvalRequest,
                cancellationToken);
        }
        finally
        {
            approvalStopwatch.Stop();
        }

        var approvalWaitDurationMs = approvalStopwatch.ElapsedMilliseconds;
        DmlApprovalRequestFactory.EnsureBoundResult(approvalRequest, approvalResult);
        if (approvalResult.Decision == DmlApprovalDecision.Pending)
        {
            var reference = string.IsNullOrWhiteSpace(approvalResult.ExternalReference)
                ? string.Empty
                : $" externalReference={approvalResult.ExternalReference}";
            return new TypedDmlExecutionTiming(
                $"DML transaction approval is pending external review. No database changes were committed.{reference}",
                approvalWaitDurationMs,
                session.Challenge.AffectedRows,
                false);
        }

        if (approvalResult.Decision != DmlApprovalDecision.Approved)
        {
            return new TypedDmlExecutionTiming(
                approvalResult.Reason ?? "DML transaction execution cancelled by approval provider.",
                approvalWaitDurationMs,
                session.Challenge.AffectedRows,
                false);
        }

        var currentPolicy = securityPolicyRuntimeState.GetCurrent();
        var currentAllowedTables = resolveAllowedTables();
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
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
                commit.Committed);
        }
    }
}
