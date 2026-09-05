using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Admin.Service.Interfaces;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.SqlParsing;
using ModelContextProtocol.Protocol;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace HsSqlAgent.Server.Tools;

/// <summary>
/// Interactive approval orchestration for one or more ordered DML statements. One approval covers
/// the complete transaction; commit either persists every statement or rolls the entire set back.
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
        IDmlApprovalClient? approvalClient,
        string approvalTitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parsedBatch);
        if (approvalClient?.SupportsElicitation != true)
            return new TypedDmlExecutionTiming(
                "Error: This MCP client does not support the interactive confirmation required for DML execution.",
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

        var message = new StringBuilder()
            .Append("## ").Append(approvalTitle).Append("\n\n")
            .Append("**Atomic transaction — ").Append(session.Statements.Length)
            .Append(" statement(s), ").Append(session.Challenge.AffectedRows)
            .Append(" total affected row(s)**\n\n")
            .Append("All statements commit together. Any revalidation or execution failure rolls back the entire transaction.\n");

        for (var i = 0; i < session.Statements.Length; i++)
        {
            var statement = session.Statements[i];
            message.Append("\n### ").Append(i + 1).Append(". ")
                .Append(statement.Plan.Operation.ToString().ToUpperInvariant())
                .Append(" on `").Append(statement.Plan.TableName).Append("` — ")
                .Append(statement.Preview.AffectedRows).Append(" row(s)\n\n")
                .Append(JsonSerializer.Serialize(statement.Preview.Rows)).Append("\n");
        }

        var approvalStopwatch = Stopwatch.StartNew();
        ElicitResult elicitResult;
        try
        {
            elicitResult = await approvalClient.ElicitAsync(new ElicitRequestParams
            {
                Message = message.ToString(),
                RequestedSchema = new RequestSchema
                {
                    Properties =
                    {
                        ["approve"] = new BooleanSchema
                        {
                            Title = "Approve atomic transaction",
                            Description =
                                $"Commit all {session.Statements.Length} statement(s) affecting {session.Challenge.AffectedRows} approved row(s), or commit none."
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
            return new TypedDmlExecutionTiming(
                "DML transaction execution cancelled by user.",
                approvalWaitDurationMs,
                session.Challenge.AffectedRows,
                false);

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
