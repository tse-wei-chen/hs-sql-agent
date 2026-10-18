using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.SqlParsing;
using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlAgent.Service.Interfaces;

namespace HsSqlAgent.Server.Tools;

public class CustomToolProxy(
    string name,
    ICustomSqlToolService customSqlToolService,
    IHttpContextAccessor httpContextAccessor,
    ISqlProviderFactory sqlProviderFactory,
    IAuditService auditService,
    IQueryValueParserService queryValueParserService,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter sqlConcurrencyLimiter,
    ITypedQueryRuntime? typedQueryRuntime = null,
    TypedDmlRuntime? typedDmlRuntime = null,
    IDmlApprovalProvider? dmlApprovalProvider = null,
    IDmlApprovalCompletionSink? dmlApprovalCompletionSink = null)
{
    private readonly string _name = name;
    private readonly ICustomSqlToolService _customSqlToolService = customSqlToolService;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlProviderFactory _sqlProviderFactory = sqlProviderFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IQueryValueParserService _queryValueParserService = queryValueParserService;
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly ISqlExecutionConcurrencyLimiter _sqlConcurrencyLimiter = sqlConcurrencyLimiter;
    private readonly ITypedQueryRuntime _typedQueryRuntime = typedQueryRuntime ?? new TypedQueryRuntime();
    private readonly TypedDmlRuntime _typedDmlRuntime = typedDmlRuntime ?? new TypedDmlRuntime();
    private readonly IDmlApprovalProvider? _dmlApprovalProvider = dmlApprovalProvider;
    private readonly IDmlApprovalCompletionSink? _dmlApprovalCompletionSink = dmlApprovalCompletionSink;

    public async Task<string> Execute(
        JsonElement arguments,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
        => await ExecuteCore(
            arguments,
            DmlApprovalProviderResolver.Resolve(
                _dmlApprovalProvider,
                server is null ? null : new McpDmlApprovalClient(server)),
            cancellationToken);

    internal async Task<string> Execute(
        JsonElement arguments,
        IDmlApprovalClient? approvalClient,
        CancellationToken cancellationToken = default)
        => await ExecuteCore(
            arguments,
            DmlApprovalProviderResolver.Resolve(_dmlApprovalProvider, approvalClient),
            cancellationToken);

    private async Task<string> ExecuteCore(
        JsonElement arguments,
        IDmlApprovalProvider? approvalProvider,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long approvalWaitDurationMs = 0;
        int? queryReturnedRows = null;
        int? dmlAffectedRows = null;
        var parameters = new Dictionary<string, object?>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
                parameters[prop.Name] = _queryValueParserService.UnwrapJsonElement(prop.Value);
        }

        CustomSqlTool? tool = null;
        QueryFacts? auditQueryFacts = null;
        SqlAgentToolType? auditQueryDialect = null;
        ParsedDmlBatch? auditDml = null;
        string renderedSql = string.Empty;
        try
        {
            McpSqlExecutionScope.EnsureToolAllowed(_httpContextAccessor.HttpContext, _name);
            var sqlConfig = ResolveSqlConfig();
            if (string.IsNullOrWhiteSpace(sqlConfig.Provider) || string.IsNullOrWhiteSpace(sqlConfig.ConnectionString))
            {
                var error = "Error: SQL configuration (provider/connection string) is missing.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            if (!Enum.TryParse<SqlAgentToolType>(sqlConfig.Provider, true, out var dbType))
            {
                var error = $"Error: Invalid SQL provider '{sqlConfig.Provider}'.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            var scope = McpSqlExecutionScope.Capture(_httpContextAccessor.HttpContext, _name, dbType);
            var dbManagementId = scope.DbManagementId;
            tool = await _customSqlToolService.GetPublishedToolByNameAsync(
                _name, dbManagementId, cancellationToken);
            if (tool is null)
            {
                var error = $"Error: Published tool '{_name}' is not available for this database.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            renderedSql = CustomToolSqlTemplate.Render(tool.SqlTemplate, tool.ParametersJson, parameters);
            var provider = _sqlProviderFactory.GetProvider(dbType);
            string result;
            var isQuery = string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase);
            var isDml = string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);

            if (isQuery)
            {
                auditQueryDialect = dbType;
                await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
                {
                    if (lease is null)
                        throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                    QueryExecutionResult execution;
                    var decision = SqlExecutionPolicyDecision.ForQuery(
                        scope, _securityPolicyRuntimeState.GetCurrent());
                    var securityPolicy = decision.PolicySnapshot;
                    var allowedTables = decision.AllowedTables;
                    if (decision.AllowedQueryColumns is { } allowedColumns)
                    {
                        if (_typedQueryRuntime is not TypedQueryRuntime restrictedRuntime)
                            throw new UnauthorizedAccessException(
                                "Restricted column enforcement is unavailable in the configured query runtime.");
                        var compiledExecution = await restrictedRuntime.ExecuteWithRestrictedColumnsAsync(
                            provider, sqlConfig.ConnectionString, renderedSql, dbType,
                            securityPolicy, allowedTables, allowedColumns, cancellationToken);
                        auditQueryFacts = compiledExecution.Facts;
                        execution = compiledExecution.Execution;
                    }
                    else if (_typedQueryRuntime is ITypedQueryRuntimeFacts factsRuntime)
                    {
                        var compiledExecution = await factsRuntime.ExecuteWithFactsAsync(
                            provider, sqlConfig.ConnectionString, renderedSql, dbType,
                            securityPolicy, allowedTables, cancellationToken);
                        auditQueryFacts = compiledExecution.Facts;
                        execution = compiledExecution.Execution;
                    }
                    else
                    {
                        auditQueryFacts = SqlCoreInspection.GetQueryFacts(renderedSql, dbType);
                        execution = await _typedQueryRuntime.ExecuteAsync(
                            provider, sqlConfig.ConnectionString, renderedSql, dbType,
                            securityPolicy, allowedTables, cancellationToken);
                    }
                    queryReturnedRows = execution.RowCount;
                    result = JsonSerializer.Serialize(execution.Rows);
                }
            }
            else if (isDml)
            {
                scope.EnsureDmlColumnPolicySupported();
                var accessKeyId = scope.AccessKeyId;
                var parsedDml = await _typedDmlRuntime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
                    provider,
                    sqlConfig.ConnectionString,
                    renderedSql,
                    dbType,
                    cancellationToken);
                auditDml = parsedDml;
                foreach (var statement in parsedDml.Statements)
                    TypedDmlRuntime.EnsureSupportedStatement(statement.Statement);

                var approvalContext = scope.ToApprovalContext();
                var flow = new TypedDmlTransactionApprovalFlow(
                    _typedDmlRuntime,
                    _securityPolicyRuntimeState,
                    _sqlConcurrencyLimiter,
                    () => scope.RefreshAllowedTables(_httpContextAccessor.HttpContext),
                    _dmlApprovalCompletionSink as IDurableDmlApprovalLifecycle);
                var dmlExecution = await flow.ExecuteAsync(
                    provider,
                    sqlConfig.ConnectionString,
                    parsedDml,
                    approvalContext,
                    approvalProvider,
                    $"Custom tool `{_name}`",
                    cancellationToken,
                    new DmlApprovalResumeContext(
                        renderedSql,
                        _name,
                        accessKeyId,
                        dbManagementId,
                        dbType,
                        tool.Id,
                        tool.PublishedRevisionId));
                result = dmlExecution.Result;
                approvalWaitDurationMs += dmlExecution.ApprovalWaitDurationMs;
                dmlAffectedRows = dmlExecution.AffectedRows;

                if (!dmlExecution.Committed)
                {
                    var (auditResult, approvalStatus) = dmlExecution.ApprovalDecision switch
                    {
                        DmlApprovalDecision.Pending => ("pending", "pending"),
                        DmlApprovalDecision.Rejected => ("cancelled", "declined"),
                        _ => ("failed", "not-completed")
                    };
                    await _auditService.WriteEventAsync(
                        $"mcp.{_name}.executed",
                        _name,
                        auditResult,
                        new AuditEventContext
                        {
                            ToolName = _name,
                            Operation = DmlOperationName(parsedDml),
                            DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                            AffectedRows = dmlAffectedRows,
                            ApprovalStatus = approvalStatus,
                            Definition = DescribeDml(parsedDml)
                        },
                        result,
                        cancellationToken);
                    return result;
                }
            }
            else
            {
                result = $"Error: Unsupported tool type '{tool.Type}'.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                return result;
            }

            await _auditService.WriteEventAsync(
                $"mcp.{_name}.executed",
                _name,
                "success",
                new AuditEventContext
                {
                    ToolName = _name,
                    Operation = isQuery ? "select" : auditDml is null ? null : DmlOperationName(auditDml),
                    DurationMs = isDml
                        ? ProcessingDuration(stopwatch, approvalWaitDurationMs)
                        : stopwatch.ElapsedMilliseconds,
                    ReturnedRows = isQuery ? queryReturnedRows : null,
                    AffectedRows = isDml ? dmlAffectedRows : null,
                    ApprovalStatus = isDml ? "approved" : null,
                    Definition = isQuery && auditQueryFacts != null
                        ? DescribeQuery(renderedSql, dbType, auditQueryFacts)
                        : auditDml == null ? null : DescribeDml(auditDml)
                },
                $"Type: {tool.Type}",
                cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            auditQueryFacts ??= SqlCoreInspection.TryGetQueryFactsFromException(ex);
            await _auditService.WriteEventAsync(
                $"mcp.{_name}.executed",
                _name,
                "failed",
                new AuditEventContext
                {
                    ToolName = _name,
                    Operation = auditQueryFacts != null
                        ? "select"
                        : auditDml is null ? null : DmlOperationName(auditDml),
                    DurationMs = auditDml == null
                        ? stopwatch.ElapsedMilliseconds
                        : ProcessingDuration(stopwatch, approvalWaitDurationMs),
                    ReturnedRows = queryReturnedRows,
                    AffectedRows = dmlAffectedRows,
                    ErrorCategory = ex.GetType().Name,
                    Definition = auditQueryFacts != null && auditQueryDialect.HasValue
                        ? DescribeQuery(renderedSql, auditQueryDialect.Value, auditQueryFacts)
                        : auditDml == null ? null : DescribeDml(auditDml)
                },
                ex.Message,
                cancellationToken);
            var toolType = tool?.Type ?? "Unknown";
            var suggestedTool = string.Equals(toolType, "Query", StringComparison.OrdinalIgnoreCase)
                ? "execute_query_sql"
                : "execute_dml_sql";
            return $"Error: {ex.Message}\nPlease fix the parameters or SQL template and use '{suggestedTool}' to try again.";
        }
    }

    private static long ProcessingDuration(Stopwatch stopwatch, long approvalWaitDurationMs) =>
        Math.Max(0, stopwatch.ElapsedMilliseconds - approvalWaitDurationMs);

    private static string DescribeQuery(string sql, SqlAgentToolType sourceDialect, QueryFacts facts) =>
        JsonSerializer.Serialize(new
        {
            SourceDialect = sourceDialect.ToString(),
            Span = new { Start = 0, End = sql.Length },
            ReferencedTables = facts.ReferencedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            facts.ContainsCte,
            facts.ContainsSubquery
        });

    private static string DescribeDml(ParsedDmlBatch parsedDml) =>
        JsonSerializer.Serialize(new
        {
            StatementCount = parsedDml.Count,
            Statements = parsedDml.Statements.Select((statement, index) =>
            {
                var table = statement.Statement switch
                {
                    UpdateStatement update => IdentifierText(update.Target.Name),
                    DeleteStatement delete => IdentifierText(delete.Target.Name),
                    InsertStatement insert => IdentifierText(insert.Target.Name),
                    _ => "unknown"
                };
                var fields = statement.Statement switch
                {
                    UpdateStatement updateStatement => updateStatement.Assignments
                        .Select(assignment => IdentifierText(assignment.Column)).ToArray(),
                    InsertStatement insertStatement => insertStatement.Columns.Select(IdentifierText).ToArray(),
                    _ => []
                };
                var hasWhere = statement.Statement switch
                {
                    UpdateStatement update => update.Predicate is not null,
                    DeleteStatement delete => delete.Predicate is not null,
                    _ => false
                };
                return new
                {
                    Index = index + 1,
                    Operation = DmlOperationName(statement),
                    TableName = table,
                    ValueFields = fields,
                    HasWhere = hasWhere
                };
            }).ToArray()
        });

    private static string DmlOperationName(ParsedDmlBatch parsedDml) =>
        parsedDml.Count == 1 ? DmlOperationName(parsedDml.Statements[0]) : "transaction";

    private static string DmlOperationName(ParsedStatement parsedDml) => parsedDml.Statement switch
    {
        UpdateStatement => "update",
        DeleteStatement => "delete",
        InsertStatement => "insert",
        _ => parsedDml.Statement.GetType().Name.ToLowerInvariant()
    };

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private SqlRuntimeConfig ResolveSqlConfig()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var provider = httpContext.Items[McpContextItemKeys.SqlProvider]?.ToString();
            var connectionString = httpContext.Items[McpContextItemKeys.SqlConnectionString]?.ToString();
            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(connectionString))
                return new SqlRuntimeConfig { Provider = provider, ConnectionString = connectionString };
        }
        return new SqlRuntimeConfig();
    }


}

internal interface IDmlApprovalClient
{
    bool SupportsElicitation { get; }
    ValueTask<ElicitResult> ElicitAsync(ElicitRequestParams request, CancellationToken cancellationToken);
}

internal sealed class McpDmlApprovalClient(McpServer server) : IDmlApprovalClient
{
    public bool SupportsElicitation => server.ClientCapabilities?.Elicitation != null;
    public ValueTask<ElicitResult> ElicitAsync(ElicitRequestParams request, CancellationToken cancellationToken) =>
        server.ElicitAsync(request, cancellationToken);
}
