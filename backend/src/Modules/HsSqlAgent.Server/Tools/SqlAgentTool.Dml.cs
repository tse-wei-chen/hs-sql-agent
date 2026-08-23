using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Server;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Validation;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    [McpServerTool, Description(@"
        Execute one UPDATE or DELETE SQL statement through the typed DML approval pipeline.
        The server parses and validates the statement, compiles an immutable mutation command,
        previews the exact primary-key row set, and presents it for interactive approval.

        Commit revalidates the current security policy, table authorization and row identities inside
        the same transaction before executing the already-approved compiled command. INSERT remains
        unavailable until its production approval semantics are defined.
    ")]
    public async Task<string> ExecuteDmlSql(
        [Description("A single UPDATE or DELETE SQL statement to parse, validate, preview, and approve.")]
        string sql,
        McpServer server,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long approvalWaitDurationMs = 0;
        DmlDefinition? dml = null;
        int? affectedRowCount = null;
        try
        {
            ValidateToolAccess("execute_dml_sql");
            if (string.IsNullOrWhiteSpace(sql))
                return "Error: SQL is missing.";

            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";

            dml = SqlDefinitionParser.ParseDml(sql, dbType);
            var dmlErrors = DefinitionValidator.Validate(dml);
            if (dmlErrors.Count > 0)
                return "Validation failed:\n" + string.Join("\n", dmlErrors);

            if (dml.Operation is not (DmlOperation.Update or DmlOperation.Delete))
            {
                throw new NotSupportedException(
                    "The production typed DML path currently supports UPDATE and DELETE only. INSERT remains fail-closed until its production approval semantics are defined.");
            }

            var provider = _sqlProviderFactory.GetProvider(dbType);
            var flow = new TypedDmlApprovalFlow(
                new TypedDmlRuntime(),
                _securityPolicyRuntimeState,
                _sqlConcurrencyLimiter,
                ResolveTableWhitelist);
            var execution = await flow.ExecuteAsync(
                provider,
                sqlConfig.ConnectionString,
                dml,
                new McpDmlApprovalClient(server),
                $"{dml.Operation} on `{dml.TableName}`",
                cancellationToken);

            approvalWaitDurationMs = execution.ApprovalWaitDurationMs;
            affectedRowCount = execution.AffectedRows;
            if (!execution.Committed)
            {
                var cancelled = execution.Result.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
                await WriteDmlAuditAsync(
                    dml,
                    cancelled ? "cancelled" : "failed",
                    cancelled ? "declined" : "not-completed",
                    stopwatch,
                    approvalWaitDurationMs,
                    affectedRowCount,
                    execution.Result,
                    cancellationToken);
                return execution.Result;
            }

            await WriteDmlAuditAsync(
                dml,
                "success",
                "interactive-accepted",
                stopwatch,
                approvalWaitDurationMs,
                affectedRowCount,
                $"Operation: {dml.Operation} (committed after typed policy and row-set revalidation)",
                cancellationToken);
            return execution.Result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteEventAsync(
                "mcp.dml.executed",
                dml?.TableName ?? "unknown",
                "failed",
                new AuditEventContext
                {
                    ToolName = "execute_dml_sql",
                    Operation = dml?.Operation.ToString().ToLowerInvariant(),
                    DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                    AffectedRows = affectedRowCount,
                    ApprovalStatus = "not-completed",
                    ErrorCategory = ex.GetType().Name,
                    Definition = dml == null ? null : DescribeDml(dml)
                },
                ex.Message,
                cancellationToken);
            return $"Error executing DML: {ex.Message}";
        }
    }

    private async Task WriteDmlAuditAsync(
        DmlDefinition dml,
        string result,
        string approvalStatus,
        Stopwatch stopwatch,
        long approvalWaitDurationMs,
        int? affectedRows,
        string detail,
        CancellationToken cancellationToken)
    {
        await _auditService.WriteEventAsync(
            "mcp.dml.executed",
            dml.TableName,
            result,
            new AuditEventContext
            {
                ToolName = "execute_dml_sql",
                Operation = dml.Operation.ToString().ToLowerInvariant(),
                DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                AffectedRows = affectedRows,
                ApprovalStatus = approvalStatus,
                ErrorCategory = result == "failed" ? "PolicyOrExecutionDenied" : null,
                Definition = DescribeDml(dml)
            },
            detail,
            cancellationToken);
    }

    private static string DescribeDml(DmlDefinition definition) =>
        JsonSerializer.Serialize(new
        {
            Operation = definition.Operation.ToString(),
            definition.TableName,
            ValueFields = definition.Values?.Select(x => x.FieldName).ToArray() ?? [],
            WhereConditionCount = definition.WhereConditions?.Count ?? 0
        });

    private static long ProcessingDuration(Stopwatch stopwatch, long approvalWaitDurationMs) =>
        Math.Max(0, stopwatch.ElapsedMilliseconds - approvalWaitDurationMs);
}
