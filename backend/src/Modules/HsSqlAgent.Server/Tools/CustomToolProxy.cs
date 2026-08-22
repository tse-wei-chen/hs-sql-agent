using System.Text.Json;
using System.Diagnostics;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Validation;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace HsSqlAgent.Server.Tools;

public class CustomToolProxy(
    string name,
    ICustomSqlToolService customSqlToolService,
    IHttpContextAccessor httpContextAccessor,
    ISqlStrategyFactory sqlStrategyFactory,
    IAuditService auditService,
    IQueryValueParserService queryValueParserService,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter sqlConcurrencyLimiter)
{
    private readonly string _name = name;
    private readonly ICustomSqlToolService _customSqlToolService = customSqlToolService;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlStrategyFactory _sqlStrategyFactory = sqlStrategyFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IQueryValueParserService _queryValueParserService = queryValueParserService;
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly ISqlExecutionConcurrencyLimiter _sqlConcurrencyLimiter = sqlConcurrencyLimiter;

    public async Task<string> Execute(
        JsonElement arguments,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
        => await ExecuteCore(
            arguments,
            server is null ? null : new McpDmlApprovalClient(server),
            cancellationToken);

    internal async Task<string> Execute(
        JsonElement arguments,
        IDmlApprovalClient? approvalClient,
        CancellationToken cancellationToken = default)
        => await ExecuteCore(arguments, approvalClient, cancellationToken);

    private async Task<string> ExecuteCore(
        JsonElement arguments,
        IDmlApprovalClient? approvalClient,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long approvalWaitDurationMs = 0;
        int? dmlAffectedRows = null;
        var parameters = new Dictionary<string, object?>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
                parameters[prop.Name] = _queryValueParserService.UnwrapJsonElement(prop.Value);
        }

        CustomSqlTool? tool = null;
        QueryDefinition? auditQuery = null;
        DmlDefinition? auditDml = null;
        string renderedSql = "";
        try
        {
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
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);

            string result;
            bool isQuery = string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase);
            bool isDml = string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);

            if (isQuery)
            {
                var queryDef = SqlDefinitionParser.ParseQuery(SqlAgentTool.NormalizeSql(renderedSql), dbType);
                auditQuery = queryDef;
                ValidateAllTableAccess(queryDef);

                var qErrors = DefinitionValidator.Validate(queryDef);
                if (qErrors.Count > 0)
                {
                    result = "Validation failed:\n" + string.Join("\n", qErrors);
                    await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                    return result;
                }

                await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
                {
                    if (lease is null)
                        throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                    result = await strategy.ExecuteQueryAsync(
                        queryDef,
                        sqlConfig.ConnectionString,
                        ResolveExecutionPolicy(),
                        cancellationToken);
                }
            }
            else if (isDml)
            {
                var dmlDef = SqlDefinitionParser.ParseDml(renderedSql, dbType);
                auditDml = dmlDef;

                var dmlErrors = DefinitionValidator.Validate(dmlDef);
                if (dmlErrors.Count > 0)
                {
                    result = "Validation failed:\n" + string.Join("\n", dmlErrors);
                    await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                    return result;
                }

                if (dmlDef.Operation is not (DmlOperation.Update or DmlOperation.Delete))
                {
                    throw new NotSupportedException(
                        "Published Custom Tool DML currently supports UPDATE and DELETE through the typed approval pipeline. INSERT remains fail-closed until its production approval semantics are defined.");
                }

                var flow = new TypedDmlApprovalFlow(
                    new TypedDmlRuntime(),
                    _securityPolicyRuntimeState,
                    _sqlConcurrencyLimiter,
                    ResolveTableWhitelist);
                var dmlExecution = await flow.ExecuteAsync(
                    strategy,
                    sqlConfig.ConnectionString,
                    dmlDef,
                    approvalClient,
                    $"Custom tool `{_name}`",
                    cancellationToken);
                result = dmlExecution.Result;
                approvalWaitDurationMs += dmlExecution.ApprovalWaitDurationMs;
                dmlAffectedRows = dmlExecution.AffectedRows;

                if (!dmlExecution.Committed)
                {
                    var auditResult = result.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                        ? "cancelled"
                        : "failed";
                    await _auditService.WriteEventAsync(
                        $"mcp.{_name}.executed",
                        _name,
                        auditResult,
                        new AuditEventContext
                        {
                            ToolName = _name,
                            Operation = dmlDef.Operation.ToString().ToLowerInvariant(),
                            DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                            AffectedRows = dmlAffectedRows,
                            ApprovalStatus = auditResult == "cancelled" ? "declined" : "not-completed",
                            Definition = DescribeDml(dmlDef)
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
                    Operation = isQuery ? "select" : auditDml?.Operation.ToString().ToLowerInvariant(),
                    DurationMs = isDml
                        ? ProcessingDuration(stopwatch, approvalWaitDurationMs)
                        : stopwatch.ElapsedMilliseconds,
                    ReturnedRows = isQuery ? CountJsonRows(result) : null,
                    AffectedRows = isDml ? dmlAffectedRows : null,
                    ApprovalStatus = isDml ? "interactive-accepted" : null,
                    Definition = isQuery && auditQuery != null
                        ? DescribeQuery(auditQuery)
                        : auditDml == null ? null : DescribeDml(auditDml)
                },
                $"Type: {tool.Type}",
                cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteEventAsync(
                $"mcp.{_name}.executed",
                _name,
                "failed",
                new AuditEventContext
                {
                    ToolName = _name,
                    Operation = auditQuery != null ? "select" : auditDml?.Operation.ToString().ToLowerInvariant(),
                    DurationMs = auditDml == null
                        ? stopwatch.ElapsedMilliseconds
                        : ProcessingDuration(stopwatch, approvalWaitDurationMs),
                    AffectedRows = dmlAffectedRows,
                    ErrorCategory = ex.GetType().Name,
                    Definition = auditQuery != null
                        ? DescribeQuery(auditQuery)
                        : auditDml == null ? null : DescribeDml(auditDml)
                },
                ex.Message,
                cancellationToken);
            var toolType = tool?.Type ?? "Unknown";
            var suggestedTool = string.Equals(toolType, "Query", StringComparison.OrdinalIgnoreCase) ? "execute_query_sql" : "execute_dml_sql";
            return $"Error: {ex.Message}\nPlease fix the parameters or SQL template and use '{suggestedTool}' to try again.";
        }
    }

    private static long ProcessingDuration(Stopwatch stopwatch, long approvalWaitDurationMs)
        => Math.Max(0, stopwatch.ElapsedMilliseconds - approvalWaitDurationMs);

    private SqlExecutionPolicy ResolveExecutionPolicy()
    {
        var policy = _securityPolicyRuntimeState.GetCurrent();
        return new SqlExecutionPolicy
        {
            QueryMaxRows = policy.QueryMaxRows,
            QueryTimeoutSeconds = policy.QueryTimeoutSeconds,
            RequireWhereForUpdate = policy.RequireWhereForUpdate,
            RequireWhereForDelete = policy.RequireWhereForDelete,
            AllowFullTableUpdate = policy.AllowFullTableUpdate,
            AllowFullTableDelete = policy.AllowFullTableDelete,
            DmlMaxAffectedRows = policy.DmlMaxAffectedRows
        };
    }

    private static int? CountJsonRows(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeQuery(QueryDefinition definition)
        => JsonSerializer.Serialize(new
        {
            definition.TableName,
            SelectColumnCount = definition.SelectColumns?.Count ?? 0,
            WhereConditionCount = definition.WhereColumnsAndValues?.Count ?? 0,
            JoinCount = definition.Joins?.Count ?? 0,
            definition.Limit,
            definition.Offset
        });

    private static string DescribeDml(DmlDefinition definition)
        => JsonSerializer.Serialize(new
        {
            Operation = definition.Operation.ToString(),
            definition.TableName,
            ValueFields = definition.Values?.Select(x => x.FieldName).ToArray() ?? [],
            WhereConditionCount = definition.WhereConditions?.Count ?? 0
        });

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
            && value is int id
            ? id
            : null;
    }

    private HashSet<string>? ResolveTableWhitelist()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return null;
        var tableWhitelist = context.Items[McpContextItemKeys.TableWhitelist] as string;
        if (string.IsNullOrWhiteSpace(tableWhitelist)) return null;
        return tableWhitelist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ValidateToolAccess()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return;
        var allowedTools = context.Items[McpContextItemKeys.AllowedTools] as string;
        if (string.IsNullOrWhiteSpace(allowedTools)) return;
        var allowed = allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, _name, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
            throw new UnauthorizedAccessException($"API key does not have permission to use tool: {_name}");
    }

    private void ValidateAllTableAccess(QueryDefinition queryDef)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SqlAgentTool.CollectReferencesAndAliases(queryDef.TableName, queryDef.Joins, queryDef.CombineConditions, queryDef.CteConditions, queryDef.FromQuery, queryDef.SelectColumns, queryDef.WhereColumnsAndValues, referenced, aliases);
        SqlAgentTool.CollectFromHavingConditions(queryDef.HavingConditions, referenced, aliases);
        SqlAgentTool.CollectFromOrderByConditions(queryDef.OrderByColumns, referenced, aliases);
        SqlAgentTool.CollectFromGroupByConditions(queryDef.GroupByConditions, referenced, aliases);
        var violations = referenced.Where(t => !whitelist.Contains(t)).ToList();
        if (violations.Count > 0)
            throw new UnauthorizedAccessException($"API key does not have permission to access table(s): {string.Join(", ", violations)}");
    }
}

internal interface IDmlApprovalClient
{
    bool SupportsElicitation { get; }

    ValueTask<ElicitResult> ElicitAsync(
        ElicitRequestParams request,
        CancellationToken cancellationToken);
}

internal sealed class McpDmlApprovalClient(McpServer server) : IDmlApprovalClient
{
    public bool SupportsElicitation => server.ClientCapabilities?.Elicitation != null;

    public ValueTask<ElicitResult> ElicitAsync(
        ElicitRequestParams request,
        CancellationToken cancellationToken)
        => server.ElicitAsync(request, cancellationToken);
}
