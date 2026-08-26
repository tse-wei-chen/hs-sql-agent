using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Interfaces;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Protocol;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace HsSqlAgent.Server.Tools;

/// <summary>
/// Shared interactive approval orchestration for server DML entry points. The flow never accepts
/// or produces a legacy confirmation token; the only commit credential is the typed one-time
/// challenge embedded in the preview session. Provider identity and the parser-native mutation are
/// explicit and strategy-free.
/// </summary>
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
        IDmlApprovalClient? approvalClient,
        string approvalTitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(parsedMutation);

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
                provider,
                connectionString,
                parsedMutation,
                previewPolicy,
                previewAllowedTables,
                cancellationToken);
        }

        var affectedRows = session.Preview.AffectedRows;
        var previewJson = JsonSerializer.Serialize(session.Preview.Rows);
        var operation = session.Plan.Operation.ToString().ToUpperInvariant();
        var approvalStopwatch = Stopwatch.StartNew();
        ElicitResult elicitResult;
        try
        {
            elicitResult = await approvalClient.ElicitAsync(new ElicitRequestParams
            {
                Message =
                    $"## {approvalTitle}\n\n" +
                    $"**{operation} on `{session.Plan.TableName}` — {affectedRows} row(s) affected**\n\n" +
                    $"### Impact preview\n\n{previewJson}",
                RequestedSchema = new RequestSchema
                {
                    Properties =
                    {
                        ["approve"] = new BooleanSchema
                        {
                            Title = "Approve execution",
                            Description =
                                $"This will **{operation} {affectedRows} row(s)** in `{session.Plan.TableName}`."
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
                provider,
                connectionString,
                session,
                currentPolicy,
                currentAllowedTables,
                cancellationToken);
            var result = commit.Committed
                ? $"Success | affectedRows={commit.AffectedRows} | {commit.Message}"
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

internal readonly record struct TypedDmlExecutionTiming(
    string Result,
    long ApprovalWaitDurationMs,
    int? AffectedRows,
    bool Committed);
