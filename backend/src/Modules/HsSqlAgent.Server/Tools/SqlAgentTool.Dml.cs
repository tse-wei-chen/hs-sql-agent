using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Models;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.SqlParsing;
using ModelContextProtocol.Server;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    [McpServerTool(UseStructuredContent = true, ReadOnly = false), Description(@"
        Execute one or more UPDATE, DELETE, or INSERT VALUES statements through the typed DML approval pipeline.
        Multiple statements are separated by semicolons and are approved once, then committed atomically in their
        original order. The server owns BEGIN/COMMIT/ROLLBACK; transaction-control SQL is not accepted.

        UPDATE/DELETE approval binds to exact primary-key row sets and revalidates each row set immediately before
        its mutation inside the transaction. INSERT VALUES approval binds to immutable literal payloads and exact
        compiled commands. If any later row set changes because of an earlier statement, the entire transaction
        fails closed and rolls back. INSERT ... SELECT remains unavailable until source-rowset approval semantics are defined.
    ")]
    public async Task<McpDmlToolResult> ExecuteDmlSql(
        [Description("One or more semicolon-separated UPDATE, DELETE, or INSERT VALUES statements. Multiple statements execute as one atomic transaction.")]
        string sql,
        McpServer server,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long approvalWaitDurationMs = 0;
        ParsedDmlBatch? parsedBatch = null;
        int? affectedRowCount = null;
        string? providerName = null;
        try
        {
            ValidateToolAccess("execute_dml_sql");
            if (string.IsNullOrWhiteSpace(sql))
            {
                return McpDmlToolResult.Failed(
                    null,
                    0,
                    "SQL is missing.",
                    new McpToolError(
                        "validation.sql_missing",
                        "SQL is missing.",
                        "Input"));
            }

            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            {
                return McpDmlToolResult.Failed(
                    null,
                    0,
                    InvalidSqlConfigurationMessage,
                    new McpToolError(
                        "configuration.invalid",
                        InvalidSqlConfigurationMessage,
                        "Configuration"));
            }
            providerName = dbType.ToString();

            var accessKeyId = ResolveAccessKeyId()
                              ?? throw new UnauthorizedAccessException("DML approval requires a stable MCP access-key identity.");
            var dbManagementId = ResolveDbManagementId()
                                 ?? throw new UnauthorizedAccessException("DML approval requires a stable target database identity.");

            var provider = _sqlProviderFactory.GetProvider(dbType);
            parsedBatch = await _typedDmlRuntime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
                provider, sqlConfig.ConnectionString, sql, dbType, cancellationToken);
            foreach (var statement in parsedBatch.Statements)
                TypedDmlRuntime.EnsureSupportedStatement(statement.Statement);

            var approvalContext = DmlApprovalExecutionContextResolver.FromMcp(_httpContextAccessor.HttpContext, dbType);
            var flow = new TypedDmlTransactionApprovalFlow(
                _typedDmlRuntime,
                _securityPolicyRuntimeState,
                _sqlConcurrencyLimiter,
                ResolveTableWhitelist,
                _dmlApprovalCompletionSink as IDurableDmlApprovalLifecycle);
            var descriptor = DescribeBatch(parsedBatch);
            var approvalTitle = parsedBatch.Count == 1
                ? $"{descriptor.Operation} on `{descriptor.Resource}`"
                : $"Atomic DML transaction ({parsedBatch.Count} statements)";
            var approvalProvider = DmlApprovalProviderResolver.Resolve(
                _dmlApprovalProvider,
                new McpDmlApprovalClient(server));
            var execution = await flow.ExecuteAsync(
                provider,
                sqlConfig.ConnectionString,
                parsedBatch,
                approvalContext,
                approvalProvider,
                approvalTitle,
                cancellationToken,
                new DmlApprovalResumeContext(
                    sql,
                    "execute_dml_sql",
                    accessKeyId,
                    dbManagementId,
                    dbType));

            approvalWaitDurationMs = execution.ApprovalWaitDurationMs;
            affectedRowCount = execution.AffectedRows;
            if (!execution.Committed)
            {
                var (auditResult, approvalStatus) = execution.ApprovalDecision switch
                {
                    DmlApprovalDecision.Pending => ("pending", "pending"),
                    DmlApprovalDecision.Rejected => ("cancelled", "declined"),
                    _ => ("failed", "not-completed")
                };
                await WriteDmlAuditAsync(
                    parsedBatch,
                    auditResult,
                    approvalStatus,
                    stopwatch,
                    approvalWaitDurationMs,
                    affectedRowCount,
                    execution.Result,
                    cancellationToken);

                return execution.ApprovalDecision switch
                {
                    DmlApprovalDecision.Pending => McpDmlToolResult.Pending(
                        providerName,
                        parsedBatch.Count,
                        execution.AffectedRows,
                        execution.ApprovalWaitDurationMs,
                        execution.ApprovalRequestId,
                        execution.ApprovalExternalReference,
                        execution.Result),
                    DmlApprovalDecision.Rejected => McpDmlToolResult.Rejected(
                        providerName,
                        parsedBatch.Count,
                        execution.AffectedRows,
                        execution.ApprovalWaitDurationMs,
                        execution.ApprovalRequestId,
                        execution.Result),
                    _ => McpDmlToolResult.Failed(
                        providerName,
                        parsedBatch.Count,
                        execution.Result,
                        new McpToolError(
                            execution.ErrorCode ?? "dml.execution_failed",
                            execution.Result,
                            execution.ErrorStage ?? "Execution",
                            execution.Retryable),
                        execution.AffectedRows,
                        execution.ApprovalWaitDurationMs,
                        ApprovalDecisionText(execution.ApprovalDecision),
                        execution.ApprovalRequestId,
                        execution.ApprovalExternalReference)
                };
            }

            await WriteDmlAuditAsync(
                parsedBatch,
                "success",
                "approved",
                stopwatch,
                approvalWaitDurationMs,
                affectedRowCount,
                parsedBatch.Count == 1
                    ? "Committed after typed policy and approval revalidation."
                    : $"Committed atomic transaction with {parsedBatch.Count} statements after per-statement revalidation.",
                cancellationToken);
            return McpDmlToolResult.CommittedResult(
                providerName,
                parsedBatch.Count,
                execution.AffectedRows,
                execution.ApprovalWaitDurationMs,
                execution.ApprovalRequestId,
                execution.ReturnedRows,
                execution.Result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var descriptor = parsedBatch is null ? null : DescribeBatch(parsedBatch);
            await _auditService.WriteEventAsync(
                "mcp.dml.executed",
                descriptor?.Resource ?? "unknown",
                "failed",
                new AuditEventContext
                {
                    ToolName = "execute_dml_sql",
                    Operation = descriptor?.Operation.ToLowerInvariant(),
                    DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                    AffectedRows = affectedRowCount,
                    ApprovalStatus = "not-completed",
                    ErrorCategory = ex.GetType().Name,
                    Definition = parsedBatch is null ? null : DescribeDmlBatch(parsedBatch)
                },
                ex.Message,
                cancellationToken);
            return McpDmlToolResult.Failed(
                providerName,
                parsedBatch?.Count ?? 0,
                $"Error executing DML: {ex.Message}",
                DescribeDmlError(ex),
                affectedRowCount,
                approvalWaitDurationMs);
        }
    }

    private async Task WriteDmlAuditAsync(
        ParsedDmlBatch parsedBatch,
        string result,
        string approvalStatus,
        Stopwatch stopwatch,
        long approvalWaitDurationMs,
        int? affectedRows,
        string detail,
        CancellationToken cancellationToken)
    {
        var descriptor = DescribeBatch(parsedBatch);
        await _auditService.WriteEventAsync(
            "mcp.dml.executed",
            descriptor.Resource,
            result,
            new AuditEventContext
            {
                ToolName = "execute_dml_sql",
                Operation = descriptor.Operation.ToLowerInvariant(),
                DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                AffectedRows = affectedRows,
                ApprovalStatus = approvalStatus,
                ErrorCategory = result == "failed" ? "PolicyOrExecutionDenied" : null,
                Definition = DescribeDmlBatch(parsedBatch)
            },
            detail,
            cancellationToken);
    }

    private static McpToolError DescribeDmlError(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var evidence = SqlCompileEvidence.TryGetFromException(current);
            if (evidence is not null)
            {
                return new McpToolError(
                    evidence.DecisionCode,
                    error.Message,
                    evidence.DecisionBoundary.ToString());
            }
        }

        return error switch
        {
            UnauthorizedAccessException => new McpToolError(
                "authorization.denied",
                error.Message,
                "Authorization"),
            TimeoutException => new McpToolError(
                "execution.timeout",
                error.Message,
                "Execution",
                true),
            NotSupportedException => new McpToolError(
                "dml.unsupported_statement",
                error.Message,
                "Capability"),
            InvalidOperationException when error.Message.StartsWith("Server busy:", StringComparison.Ordinal) =>
                new McpToolError(
                    "server.busy",
                    error.Message,
                    "Execution",
                    true),
            InvalidOperationException when error.Message.Contains("durable approval lifecycle", StringComparison.OrdinalIgnoreCase) =>
                new McpToolError(
                    "approval.lifecycle_unavailable",
                    error.Message,
                    "Approval"),
            _ => new McpToolError(
                "dml.execution_failed",
                error.Message,
                "Execution")
        };
    }

    private static string? ApprovalDecisionText(DmlApprovalDecision? decision) => decision switch
    {
        DmlApprovalDecision.Approved => "approved",
        DmlApprovalDecision.Pending => "pending",
        DmlApprovalDecision.Rejected => "rejected",
        _ => null
    };

    private static string DescribeDml(ParsedStatement parsedMutation)
    {
        var descriptor = DescribeMutation(parsedMutation);
        var valueFields = parsedMutation.Statement switch
        {
            UpdateStatement updateStatement => updateStatement.Assignments.Select(assignment => IdentifierText(assignment.Column)).ToArray(),
            InsertStatement insertStatement => insertStatement.Columns.Select(IdentifierText).ToArray(),
            _ => []
        };
        var hasWhere = parsedMutation.Statement switch
        {
            UpdateStatement updateWithPredicate => updateWithPredicate.Predicate is not null,
            DeleteStatement deleteWithPredicate => deleteWithPredicate.Predicate is not null,
            _ => false
        };
        return JsonSerializer.Serialize(new
        {
            descriptor.Operation,
            TableName = descriptor.Table,
            ValueFields = valueFields,
            HasWhere = hasWhere
        });
    }

    private static string DescribeDmlBatch(ParsedDmlBatch parsedBatch)
    {
        var statements = parsedBatch.Statements.Select((parsedMutation, index) =>
        {
            using var document = JsonDocument.Parse(DescribeDml(parsedMutation));
            var root = document.RootElement;
            return new
            {
                Index = index + 1,
                Operation = root.GetProperty("Operation").GetString(),
                TableName = root.GetProperty("TableName").GetString(),
                ValueFields = root.GetProperty("ValueFields").EnumerateArray().Select(value => value.GetString()).ToArray(),
                HasWhere = root.GetProperty("HasWhere").GetBoolean()
            };
        }).ToArray();
        return JsonSerializer.Serialize(new { StatementCount = parsedBatch.Count, Statements = statements });
    }

    private static DmlBatchDescriptor DescribeBatch(ParsedDmlBatch parsedBatch)
    {
        if (parsedBatch.Count == 1)
        {
            var descriptor = DescribeMutation(parsedBatch.Statements[0]);
            return new DmlBatchDescriptor(descriptor.Operation, descriptor.Table);
        }
        return new DmlBatchDescriptor("TRANSACTION", "multiple");
    }

    private static DmlDescriptor DescribeMutation(ParsedStatement parsedMutation) => parsedMutation.Statement switch
    {
        UpdateStatement updateTarget => new DmlDescriptor("UPDATE", IdentifierText(updateTarget.Target.Name)),
        DeleteStatement deleteTarget => new DmlDescriptor("DELETE", IdentifierText(deleteTarget.Target.Name)),
        InsertStatement insertTarget => new DmlDescriptor("INSERT", IdentifierText(insertTarget.Target.Name)),
        _ => new DmlDescriptor(parsedMutation.Statement.GetType().Name, "unknown")
    };

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static long ProcessingDuration(Stopwatch stopwatch, long approvalWaitDurationMs) =>
        Math.Max(0, stopwatch.ElapsedMilliseconds - approvalWaitDurationMs);

    private sealed record DmlDescriptor(string Operation, string Table);
    private sealed record DmlBatchDescriptor(string Operation, string Resource);
}
