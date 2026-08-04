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
    IConfiguration configuration,
    ISqlStrategyFactory sqlStrategyFactory,
    IAuditService auditService,
    IQueryValueParserService queryValueParserService,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter sqlConcurrencyLimiter)
{
    private readonly string _name = name;
    private readonly ICustomSqlToolService _customSqlToolService = customSqlToolService;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IConfiguration _configuration = configuration;
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
                var queryDef = SqlDefinitionParser.ParseQuery(SqlAgentTool.NormalizeSql(renderedSql));
                auditQuery = queryDef;
                if (queryDef == null)
                {
                    result = "Error: Failed to deserialize QueryDefinition.";
                    await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                    return result;
                }
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
                var dmlDef = SqlDefinitionParser.ParseDml(renderedSql);
                auditDml = dmlDef;
                if (dmlDef == null)
                {
                    result = "Error: Failed to deserialize DmlDefinition.";
                    await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                    return result;
                }
                ValidateAllTableAccess(dmlDef);

                var dmlErrors = DefinitionValidator.Validate(dmlDef);
                if (dmlErrors.Count > 0)
                {
                    result = "Validation failed:\n" + string.Join("\n", dmlErrors);
                    await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                    return result;
                }

                result = await ExecuteDmlWithApprovalAsync(
                    strategy,
                    sqlConfig.ConnectionString,
                    dmlDef,
                    approvalClient,
                    cancellationToken);

                if (!result.StartsWith("Success", StringComparison.Ordinal))
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
                            DurationMs = stopwatch.ElapsedMilliseconds,
                            AffectedRows = ParseAffectedRows(result),
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
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ReturnedRows = isQuery ? CountJsonRows(result) : null,
                    AffectedRows = isDml ? ParseAffectedRows(result) : null,
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
                    DurationMs = stopwatch.ElapsedMilliseconds,
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

    private async Task<string> ExecuteDmlWithApprovalAsync(
        ISqlStrategy strategy,
        string connectionString,
        DmlDefinition dml,
        IDmlApprovalClient? approvalClient,
        CancellationToken cancellationToken)
    {
        if (approvalClient?.SupportsElicitation != true)
        {
            return "Error: This MCP client does not support the interactive confirmation required for DML execution.";
        }

        // ConfirmToken is an internal dry-run artifact. Never trust a token
        // supplied through a stored definition or a caller-controlled parameter.
        dml.ConfirmToken = null;
        var executionPolicy = ResolveExecutionPolicy();
        string dryRunResult;
        await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
                return "Server busy: maximum concurrent SQL operations reached.";
            dryRunResult = await strategy.ExecuteDmlAsync(
                connectionString,
                dml,
                executionPolicy,
                cancellationToken);
        }
        if (!dryRunResult.StartsWith("Dry Run Result", StringComparison.Ordinal))
        {
            return dryRunResult;
        }

        var affectedMatch = System.Text.RegularExpressions.Regex.Match(
            dryRunResult,
            @"affectedRows=(\d+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(
            dryRunResult,
            @"TokenRequired=(\S+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!affectedMatch.Success || !tokenMatch.Success)
        {
            return "Error: Unable to verify the DML dry-run result.";
        }

        var affectedRows = affectedMatch.Groups[1].Value;
        var elicitResult = await approvalClient.ElicitAsync(new ElicitRequestParams
        {
            Message =
                $"Custom tool '{_name}' requests {dml.Operation} on {dml.TableName} — " +
                $"{affectedRows} row(s) affected.",
            RequestedSchema = new RequestSchema
            {
                Properties =
                {
                    ["approve"] = new BooleanSchema
                    {
                        Title = "Approve execution",
                        Description =
                            $"This will {dml.Operation.ToString().ToLowerInvariant()} " +
                            $"{affectedRows} row(s) in {dml.TableName}"
                    }
                }
            }
        }, cancellationToken);

        if (elicitResult.Action != "accept"
            || elicitResult.Content?.TryGetValue("approve", out var approveElement) != true
            || approveElement.ValueKind != JsonValueKind.True)
        {
            return "DML execution cancelled by user.";
        }

        dml.ConfirmToken = tokenMatch.Groups[1].Value;
        await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
                return "Server busy: maximum concurrent SQL operations reached.";
            return await strategy.ExecuteDmlAsync(
                connectionString,
                dml,
                executionPolicy,
                cancellationToken);
        }
    }

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

    private static int? ParseAffectedRows(string result)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            result,
            @"affectedRows=(\d+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var rows) ? rows : null;
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
        return new SqlRuntimeConfig
        {
            Provider = _configuration["SqlConfig:Provider"] ?? string.Empty,
            ConnectionString = _configuration["SqlConfig:ConnectionString"] ?? string.Empty
        };
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

    private void ValidateAllTableAccess(QueryDefinition queryDef)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(queryDef.Alias)) aliases.Add(queryDef.Alias);
        SqlAgentTool.CollectReferencesAndAliases(queryDef.TableName, queryDef.Joins, queryDef.CombineConditions, queryDef.CteConditions, queryDef.FromQuery, queryDef.SelectColumns, queryDef.WhereColumnsAndValues, referenced, aliases);
        SqlAgentTool.CollectFromHavingConditions(queryDef.HavingConditions, referenced, aliases);
        SqlAgentTool.CollectFromOrderByConditions(queryDef.OrderByColumns, referenced, aliases);
        SqlAgentTool.CollectFromGroupByConditions(queryDef.GroupByConditions, referenced, aliases);
        var violations = referenced.Where(t => !aliases.Contains(t)).Where(t => !whitelist.Contains(t)).ToList();
        if (violations.Count > 0)
            throw new UnauthorizedAccessException($"API key does not have permission to access table(s): {string.Join(", ", violations)}");
    }

    private void ValidateAllTableAccess(DmlDefinition dmlDef)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SqlAgentTool.CollectReferencesAndAliases(dmlDef.TableName, null, null, null, dmlDef.FromQuery, null, dmlDef.WhereConditions, referenced, aliases);
        var violations = referenced.Where(t => !aliases.Contains(t)).Where(t => !whitelist.Contains(t)).ToList();
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
