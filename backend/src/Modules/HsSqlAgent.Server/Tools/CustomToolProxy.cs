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

        CustomSqlTool? tool = null;
        QueryFacts? auditQueryFacts = null;
        SqlAgentToolType? auditQueryDialect = null;
        ParsedDmlBatch? auditDml = null;
        string renderedSql = string.Empty;
        try
        {
            // Parameter conversion is an untrusted-input boundary too. Do not
            // let a value-parser exception bypass the sanitized MCP response.
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in arguments.EnumerateObject())
                    parameters[prop.Name] = _queryValueParserService.UnwrapJsonElement(prop.Value);
            }

            ValidateToolAccess();
            var sqlConfig = ResolveSqlConfig();
            var dbManagementId = ResolveDbManagementId();
            if (dbManagementId is null)
            {
                var error = "Error: The authenticated key is not bound to a database.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            tool = await _customSqlToolService.GetPublishedToolByNameAsync(
                _name,
                dbManagementId.Value,
                cancellationToken);
            if (tool == null)
            {
                var error = $"Error: Published tool '{_name}' is not available for this database.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

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
                    var securityPolicy = _securityPolicyRuntimeState.GetCurrent();
                    var allowedTables = ResolveTableWhitelist();
                    if (_typedQueryRuntime is ITypedQueryRuntimeFacts factsRuntime)
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
                var accessKeyId = ResolveAccessKeyId()
                                  ?? throw new UnauthorizedAccessException("DML approval requires a stable MCP access-key identity.");
                var parsedDml = await _typedDmlRuntime.ParseDmlBatchWithVerifiedRuntimeProfileUnderLeaseAsync(
                    provider,
                    sqlConfig.ConnectionString,
                    renderedSql,
                    dbType,
                    _sqlConcurrencyLimiter,
                    cancellationToken);
                auditDml = parsedDml;
                foreach (var statement in parsedDml.Statements)
                    TypedDmlRuntime.EnsureSupportedStatement(statement.Statement);

                var approvalContext = DmlApprovalExecutionContextResolver.FromMcp(
                    _httpContextAccessor.HttpContext,
                    dbType);
                var flow = new TypedDmlTransactionApprovalFlow(
                    _typedDmlRuntime,
                    _securityPolicyRuntimeState,
                    _sqlConcurrencyLimiter,
                    ResolveTableWhitelist,
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
                        dbManagementId.Value,
                        dbType,
                        tool.Id,
                        tool.PublishedRevisionId));
                result = dmlExecution.Result;
                approvalWaitDurationMs += dmlExecution.ApprovalWaitDurationMs;
                dmlAffectedRows = dmlExecution.AffectedRows;

                // A confirmed COMMIT and an unconfirmed COMMIT/ROLLBACK are terminal
                // physical outcomes. Audit I/O must never turn either into a retryable
                // failure or lose the one-time approval correlation.
                var outcomeUnknown = !dmlExecution.Committed
                    && dmlExecution.ErrorCode is ("dml.commit_outcome_unknown" or "dml.rollback_outcome_unknown");
                if (dmlExecution.Committed || outcomeUnknown)
                {
                    return await CompleteTerminalDmlOutcomeAsync(
                        dmlExecution,
                        () => _auditService.WriteEventAsync(
                            $"mcp.{_name}.executed",
                            _name,
                            outcomeUnknown ? "outcome_unknown" : "success",
                            new AuditEventContext
                            {
                                ToolName = _name,
                                Operation = DmlOperationName(parsedDml),
                                DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                                AffectedRows = outcomeUnknown ? null : dmlAffectedRows,
                                ApprovalStatus = "approved",
                                ErrorCategory = outcomeUnknown
                                    ? (dmlExecution.ErrorCode == "dml.rollback_outcome_unknown"
                                        ? "DmlRollbackOutcomeUnknown" : "DmlCommitOutcomeUnknown")
                                    : null,
                                Definition = DescribeDml(parsedDml)
                            },
                            outcomeUnknown ? dmlExecution.Result : $"Type: {tool.Type}",
                            CancellationToken.None));
                }

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
                    if (dmlExecution.ApprovalDecision is DmlApprovalDecision.Pending or DmlApprovalDecision.Rejected)
                        return result;
                    if (dmlExecution.ErrorCode == "approval.unavailable")
                        return "Error: This MCP client does not support the required interactive DML approval.";
                    var dmlError = McpErrorResponsePolicy.ForDmlFlowFailure(
                        dmlExecution.ErrorCode, dmlExecution.ErrorStage, dmlExecution.Retryable);
                    return $"Error: {dmlError.Code} ({dmlError.Stage}): {dmlError.Message}";
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
            await McpErrorResponsePolicy.TryAuditFailureAsync(() => _auditService.WriteEventAsync(
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
                cancellationToken));
            var surface = string.Equals(tool?.Type, "DML", StringComparison.OrdinalIgnoreCase)
                ? McpErrorSurface.Dml : McpErrorSurface.Query;
            var external = McpErrorResponsePolicy.Classify(ex, surface);
            // Never suggest replaying a Custom DML operation just because the
            // secondary MCP call observed an error. Reconcile approval/audit first.
            return $"Error: {external.Code} ({external.Stage}): {external.Message}";
        }
    }

    // This boundary is reached only after the approval flow has returned a terminal
    // database outcome. Secondary telemetry must not change the physical result.
    internal static async Task<string> CompleteTerminalDmlOutcomeAsync(
        TypedDmlExecutionTiming execution,
        Func<Task> writeAudit)
    {
        ArgumentNullException.ThrowIfNull(writeAudit);
        var outcomeUnknown = !execution.Committed
            && execution.ErrorCode is ("dml.commit_outcome_unknown" or "dml.rollback_outcome_unknown");
        if (!execution.Committed && !outcomeUnknown)
            throw new ArgumentException(
                "Only a confirmed commit or an unknown physical outcome may cross this boundary.",
                nameof(execution));

        try
        {
            await writeAudit();
        }
        catch (Exception)
        {
            // Even cancellation of the original MCP request cannot undo a COMMIT,
            // confirm a ROLLBACK, or make a consumed approval safe to replay.
        }

        if (execution.Committed)
            return execution.Result;

        // The Custom Tool transport returns a string. Preserve machine-readable
        // correlation without exposing raw driver errors in the MCP response.
        return $"status=outcome_unknown; errorCode={execution.ErrorCode}; retryable=false; " +
               $"approvalRequestId={execution.ApprovalRequestId ?? "unavailable"}; " +
               "The database transaction outcome is unconfirmed. Do not retry; " +
               "reconcile database state before any further mutation.";
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

    private int? ResolveDbManagementId()
    {
        var context = _httpContextAccessor.HttpContext;
        return context?.Items.TryGetValue(McpContextItemKeys.DbManagementId, out var value) == true
               && value is int id ? id : null;
    }

    private int? ResolveAccessKeyId()
    {
        var context = _httpContextAccessor.HttpContext;
        return context?.Items.TryGetValue(McpContextItemKeys.AccessKeyId, out var value) == true
               && value is int id ? id : null;
    }

    private HashSet<string>? ResolveTableWhitelist()
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("MCP table authorization context is missing.");
        if (!context.Items.TryGetValue(McpContextItemKeys.TableWhitelist, out var whitelistValue))
            throw new UnauthorizedAccessException("MCP table authorization context is missing.");
        var tableWhitelist = whitelistValue?.ToString();
        if (string.IsNullOrWhiteSpace(tableWhitelist)) return null;
        return tableWhitelist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ValidateToolAccess()
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("MCP tool authorization context is missing.");
        if (!context.Items.TryGetValue(McpContextItemKeys.AllowedTools, out var allowedToolsValue))
            throw new UnauthorizedAccessException("MCP tool authorization context is missing.");
        var allowedTools = allowedToolsValue?.ToString();
        if (string.IsNullOrWhiteSpace(allowedTools)) return;
        if (!allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(x => string.Equals(x, _name, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"API key does not have permission to use tool: {_name}");
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
