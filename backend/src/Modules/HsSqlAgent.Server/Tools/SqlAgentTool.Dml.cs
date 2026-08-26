using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Server;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    [McpServerTool, Description(@"
        Execute one UPDATE, DELETE, or INSERT VALUES SQL statement through the typed DML approval pipeline.
        The server parses SQL directly into the Core AST, binds and validates it, compiles an immutable
        mutation command, and presents the exact impact for interactive approval.

        UPDATE/DELETE preview and commit bind approval to the exact primary-key row set and revalidate
        row identities inside the transaction. INSERT VALUES preview binds approval to the immutable
        literal payload and exact compiled command; commit verifies the approved payload row count.
        INSERT ... SELECT remains unavailable until source-rowset approval semantics are defined.
    ")]
    public async Task<string> ExecuteDmlSql(
        [Description("A single UPDATE, DELETE, or INSERT VALUES SQL statement to parse, validate, preview, and approve.")]
        string sql,
        McpServer server,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long approvalWaitDurationMs = 0;
        ParsedStatement? parsedMutation = null;
        int? affectedRowCount = null;
        try
        {
            ValidateToolAccess("execute_dml_sql");
            if (string.IsNullOrWhiteSpace(sql))
                return "Error: SQL is missing.";

            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";

            parsedMutation = CoreSqlTextParser.ParseDml(sql, dbType);
            var descriptor = DescribeMutation(parsedMutation);
            TypedDmlRuntime.EnsureSupportedStatement(parsedMutation.Statement);

            var provider = _sqlProviderFactory.GetProvider(dbType);
            var flow = new TypedDmlApprovalFlow(
                _typedDmlRuntime,
                _securityPolicyRuntimeState,
                _sqlConcurrencyLimiter,
                ResolveTableWhitelist);
            var execution = await flow.ExecuteAsync(
                provider,
                sqlConfig.ConnectionString,
                parsedMutation,
                new McpDmlApprovalClient(server),
                $"{descriptor.Operation} on `{descriptor.Table}`",
                cancellationToken);

            approvalWaitDurationMs = execution.ApprovalWaitDurationMs;
            affectedRowCount = execution.AffectedRows;
            if (!execution.Committed)
            {
                var cancelled = execution.Result.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
                await WriteDmlAuditAsync(
                    parsedMutation,
                    cancelled ? "cancelled" : "failed",
                    cancelled ? "declined" : "not-completed",
                    stopwatch,
                    approvalWaitDurationMs,
                    affectedRowCount,
                    execution.Result,
                    cancellationToken);
                return execution.Result;
            }

            var validationDetail = descriptor.Operation == "INSERT"
                ? "exact-payload approval validation"
                : "typed policy and row-set revalidation";
            await WriteDmlAuditAsync(
                parsedMutation,
                "success",
                "interactive-accepted",
                stopwatch,
                approvalWaitDurationMs,
                affectedRowCount,
                $"Operation: {descriptor.Operation} (committed after {validationDetail})",
                cancellationToken);
            return execution.Result;
        }
        catch (Exception ex)
        {
            var descriptor = parsedMutation is null ? null : DescribeMutation(parsedMutation);
            await _auditService.WriteEventAsync(
                "mcp.dml.executed",
                descriptor?.Table ?? "unknown",
                "failed",
                new AuditEventContext
                {
                    ToolName = "execute_dml_sql",
                    Operation = descriptor?.Operation.ToLowerInvariant(),
                    DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                    AffectedRows = affectedRowCount,
                    ApprovalStatus = "not-completed",
                    ErrorCategory = ex.GetType().Name,
                    Definition = parsedMutation is null ? null : DescribeDml(parsedMutation)
                },
                ex.Message,
                cancellationToken);
            return $"Error executing DML: {ex.Message}";
        }
    }

    private async Task WriteDmlAuditAsync(
        ParsedStatement parsedMutation,
        string result,
        string approvalStatus,
        Stopwatch stopwatch,
        long approvalWaitDurationMs,
        int? affectedRows,
        string detail,
        CancellationToken cancellationToken)
    {
        var descriptor = DescribeMutation(parsedMutation);
        await _auditService.WriteEventAsync(
            "mcp.dml.executed",
            descriptor.Table,
            result,
            new AuditEventContext
            {
                ToolName = "execute_dml_sql",
                Operation = descriptor.Operation.ToLowerInvariant(),
                DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                AffectedRows = affectedRows,
                ApprovalStatus = approvalStatus,
                ErrorCategory = result == "failed" ? "PolicyOrExecutionDenied" : null,
                Definition = DescribeDml(parsedMutation)
            },
            detail,
            cancellationToken);
    }

    private static string DescribeDml(ParsedStatement parsedMutation)
    {
        var descriptor = DescribeMutation(parsedMutation);
        var valueFields = parsedMutation.Statement switch
        {
            UpdateStatement updateStatement => updateStatement.Assignments
                .Select(assignment => IdentifierText(assignment.Column))
                .ToArray(),
            InsertStatement insertStatement => insertStatement.Columns
                .Select(IdentifierText)
                .ToArray(),
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

    private static DmlDescriptor DescribeMutation(ParsedStatement parsedMutation) =>
        parsedMutation.Statement switch
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
}
