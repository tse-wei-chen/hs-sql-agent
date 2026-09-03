using HsSqlAgent.SqlCore;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Models;
using ModelContextProtocol.Server;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    [McpServerTool, Description(@"
        Execute one SELECT SQL statement. SQL text enters the F# compiler pipeline directly, where it is parsed, bound,
        validated against table authorization and query policy, compiled to an immutable command, then executed.

        Use get_schemas/get_tables/get_columns first when you need database structure. Only send a single SELECT statement.
        Supported SQL includes JOINs, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT/OFFSET, DISTINCT, CTEs, subqueries, and UNION/INTERSECT/EXCEPT.
    ")]
    public async Task<string> ExecuteQuerySql(
        [Description("A single SELECT SQL statement to parse, validate, compile, and execute.")]
        string sql,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var sqlConfig = await ResolveSqlConfigAsync();
        if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            return InvalidSqlConfigurationMessage;

        var provider = _sqlProviderFactory.GetProvider(dbType);
        QueryFacts? auditFacts = null;
        try
        {
            ValidateToolAccess("execute_query_sql");
            if (string.IsNullOrWhiteSpace(sql))
                return "Error: SQL is missing.";

            var securityPolicy = _securityPolicyRuntimeState.GetCurrent();
            var allowedTables = ResolveTableWhitelist();
            QueryExecutionResult execution;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                if (_typedQueryRuntime is ITypedQueryRuntimeFacts factsRuntime)
                {
                    var compiledExecution = await factsRuntime.ExecuteWithFactsAsync(
                        provider,
                        sqlConfig.ConnectionString,
                        sql,
                        dbType,
                        securityPolicy,
                        allowedTables,
                        cancellationToken);
                    auditFacts = compiledExecution.Facts;
                    execution = compiledExecution.Execution;
                }
                else
                {
                    auditFacts = SqlCoreInspection.GetQueryFacts(sql, dbType);
                    execution = await _typedQueryRuntime.ExecuteAsync(
                        provider,
                        sqlConfig.ConnectionString,
                        sql,
                        dbType,
                        securityPolicy,
                        allowedTables,
                        cancellationToken);
                }
            }

            var result = JsonSerializer.Serialize(execution.Rows);
            await _auditService.WriteEventAsync(
                "mcp.query.executed",
                AuditTarget(auditFacts),
                "success",
                new AuditEventContext
                {
                    ToolName = "execute_query_sql",
                    Operation = "select",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ReturnedRows = execution.RowCount,
                    Definition = DescribeQuery(sql, dbType, auditFacts)
                },
                $"Provider: {dbType}",
                cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            auditFacts ??= SqlCoreInspection.TryGetQueryFactsFromException(ex);
            await _auditService.WriteEventAsync(
                "mcp.query.executed",
                AuditTarget(auditFacts),
                "failed",
                new AuditEventContext
                {
                    ToolName = "execute_query_sql",
                    Operation = "select",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorCategory = ex.GetType().Name,
                    Definition = auditFacts is null ? null : DescribeQuery(sql, dbType, auditFacts)
                },
                ex.Message,
                cancellationToken);
            return "Execution failed: " + ex.Message;
        }
    }

    private static string AuditTarget(QueryFacts? facts) =>
        facts?.ReferencedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
        ?? "query";

    private static string DescribeQuery(string sql, SqlAgentToolType sourceDialect, QueryFacts? facts) =>
        JsonSerializer.Serialize(new
        {
            SourceDialect = sourceDialect.ToString(),
            Span = new { Start = 0, End = sql.Length },
            ReferencedTables = facts?.ReferencedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            facts?.ContainsCte,
            facts?.ContainsSubquery
        });
}
