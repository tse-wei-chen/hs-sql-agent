using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Interfaces;
using ModelContextProtocol.Protocol;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace HsSqlAgent.Server.Tools;

/// <summary>
/// Shared interactive approval orchestration for server DML entry points. The flow never accepts
/// or produces a legacy confirmation token; the only commit credential is the typed one-time
/// challenge embedded in the preview session.
/// </summary>
internal sealed class TypedDmlApprovalFlow(
    TypedDmlRuntime runtime,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter concurrencyLimiter,
    Func<IReadOnlySet<string>?> resolveAllowedTables)
{
    public async Task<TypedDmlExecutionTiming> ExecuteAsync(
        ISqlStrategy strategy,
        string connectionString,
        DmlDefinition definition,
        IDmlApprovalClient? approvalClient,
        string approvalTitle,
        CancellationToken cancellationToken)
    {
        if (approvalClient?.SupportsElicitation != true)
        {
            return new TypedDmlExecutionTiming(
                "Error: This MCP client does not support the interactive confirmation required for DML execution.",
                0,
                null,
                false);
        }

        var previewPolicy = securityPolicyRuntimeState.GetCurrent();
        var previewAllowedTables = resolveAllowedTables();
        TypedDmlApprovalSession session;
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
            {
                return new TypedDmlExecutionTiming(
                    "Server busy: maximum concurrent SQL operations reached.",
                    0,
                    null,
                    false);
            }

            session = await runtime.PreviewAsync(
                strategy,
                connectionString,
                definition,
                previewPolicy,
                previewAllowedTables,
                cancellationToken);
        }

        var affectedRows = session.Preview.AffectedRows;
        var previewJson = JsonSerializer.Serialize(session.Preview.Rows);
        var approvalStopwatch = Stopwatch.StartNew();
        ElicitResult elicitResult;
        try
        {
            elicitResult = await approvalClient.ElicitAsync(new ElicitRequestParams
            {
                Message =
                    $"## {approvalTitle}\n\n" +
                    $"**{definition.Operation} on `{session.Plan.TableName}` — {affectedRows} row(s) affected**\n\n" +
                    $"### Impact preview\n\n{previewJson}",
                RequestedSchema = new RequestSchema
                {
                    Properties =
                    {
                        ["approve"] = new BooleanSchema
                        {
                            Title = "Approve execution",
                            Description =
                                $"This will **{definition.Operation.ToString().ToUpperInvariant()} " +
                                $"{affectedRows} row(s)** in `{session.Plan.TableName}`."
                        }
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            approvalStopwatch.Stop();
        }

        var approvalWaitDurationMs = approvalStopwatch.ElapsedMilliseconds;
        if (elicitResult.Action != "accept"
            || elicitResult.Content?.TryGetValue("approve", out var approveElement) != true
            || approveElement.ValueKind != JsonValueKind.True)
        {
            return new TypedDmlExecutionTiming(
                "DML execution cancelled by user.",
                approvalWaitDurationMs,
                affectedRows,
                false);
        }

        var currentPolicy = securityPolicyRuntimeState.GetCurrent();
        var currentAllowedTables = resolveAllowedTables();
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
            {
                return new TypedDmlExecutionTiming(
                    "Server busy: maximum concurrent SQL operations reached.",
                    approvalWaitDurationMs,
                    affectedRows,
                    false);
            }

            var commit = await runtime.CommitAsync(
                strategy,
                connectionString,
                session,
                currentPolicy,
                currentAllowedTables,
                cancellationToken);
            return new TypedDmlExecutionTiming(
                commit.Committed
                    ? $"Success | affectedRows={commit.AffectedRows} | {commit.Message}"
                    : commit.Message,
                approvalWaitDurationMs,
                commit.AffectedRows,
                commit.Committed);
        }
    }
}

internal readonly record struct TypedDmlExecutionTiming(
    string Result,
    long ApprovalWaitDurationMs,
    int? AffectedRows,
    bool Committed);
