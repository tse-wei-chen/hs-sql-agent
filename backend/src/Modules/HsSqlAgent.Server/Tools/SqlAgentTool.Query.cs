using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Models;
using ModelContextProtocol.Server;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.SqlParsing;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    [McpServerTool, Description(@"
        Execute one SELECT SQL statement. The server parses SQL directly into the Core AST, binds and validates it,
        applies the current table authorization and query policy, compiles an immutable command, then executes it.

        Use get_schemas/get_tables/get_columns first when you need database structure. Only send a single SELECT statement.
        Supported SQL includes JOINs, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT/OFFSET, DISTINCT, CTEs, subqueries, and UNION/INTERSECT/EXCEPT.
    ")]
    public async Task<string> ExecuteQuerySql(
        [Description("A single SELECT SQL statement to parse, validate, compile, and execute.")]
        string sql)
    {
        var stopwatch = Stopwatch.StartNew();
        var sqlConfig = await ResolveSqlConfigAsync();
        if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";

        var provider = _sqlProviderFactory.GetProvider(dbType);
        ParsedStatement? parsed = null;
        QueryFacts? auditFacts = null;
        try
        {
            ValidateToolAccess("execute_query_sql");
            if (string.IsNullOrWhiteSpace(sql))
                return "Error: SQL is missing.";

            parsed = CoreSqlTextParser.ParseQuery(sql, dbType);
            auditFacts = new SqlAstBinder().Bind(parsed).Facts;

            var securityPolicy = _securityPolicyRuntimeState.GetCurrent();
            var allowedTables = ResolveTableWhitelist();
            QueryExecutionResult execution;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                execution = await _typedQueryRuntime.ExecuteAsync(
                    provider,
                    sqlConfig.ConnectionString,
                    parsed,
                    securityPolicy,
                    allowedTables);
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
                    Definition = DescribeQuery(parsed, auditFacts)
                },
                $"Provider: {dbType}");
            return result;
        }
        catch (Exception ex)
        {
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
                    Definition = parsed is null ? null : DescribeQuery(parsed, auditFacts)
                },
                ex.Message);
            return "Execution failed: " + ex.Message;
        }
    }

    private static string AuditTarget(QueryFacts? facts) =>
        facts?.ReferencedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
        ?? "query";

    private static string DescribeQuery(ParsedStatement parsed, QueryFacts? facts) =>
        JsonSerializer.Serialize(new
        {
            SourceDialect = parsed.SourceDialect.ToString(),
            Span = new { parsed.Statement.Span.Start, parsed.Statement.Span.End },
            ReferencedTables = facts?.ReferencedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            facts?.ContainsCte,
            facts?.ContainsSubquery
        });
}
