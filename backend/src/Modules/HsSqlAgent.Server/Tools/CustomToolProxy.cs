using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Validation;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace HsSqlAgent.Server.Tools;

public class CustomToolProxy(string name, ICustomSqlToolService customSqlToolService, IHttpContextAccessor httpContextAccessor, IConfiguration configuration, ISqlStrategyFactory sqlStrategyFactory, IAuditService auditService, IQueryValueParserService queryValueParserService)
{
    private readonly string _name = name;
    private readonly ICustomSqlToolService _customSqlToolService = customSqlToolService;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IConfiguration _configuration = configuration;
    private readonly ISqlStrategyFactory _sqlStrategyFactory = sqlStrategyFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IQueryValueParserService _queryValueParserService = queryValueParserService;
    private static readonly JsonSerializerOptions _jsonOptions = new(McpJsonUtilities.DefaultOptions)
    {
        AllowOutOfOrderMetadataProperties = true,
        PropertyNameCaseInsensitive = true
    };

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
        var parameters = new Dictionary<string, object>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
                parameters[prop.Name] = _queryValueParserService.UnwrapJsonElement(prop.Value);
        }

        CustomSqlTool? tool = null;
        string finalDefinitionJson = "";
        try
        {
            var sqlConfig = ResolveSqlConfig();
            tool = await _customSqlToolService.GetToolByNameAsync(_name);
            if (tool == null)
            {
                var error = $"Error: Tool '{_name}' not found.";
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

            finalDefinitionJson = ReplaceParameters(tool.DefinitionJson, parameters);
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);

            string result;
            bool isQuery = string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase);
            bool isDml = string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);

            if (isQuery)
            {
                var queryDef = JsonSerializer.Deserialize<QueryDefinition>(finalDefinitionJson, _jsonOptions);
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

                result = await strategy.ExecuteQueryAsync(queryDef, sqlConfig.ConnectionString, cancellationToken);
            }
            else if (isDml)
            {
                var dmlDef = JsonSerializer.Deserialize<DmlDefinition>(finalDefinitionJson, _jsonOptions);
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
                    await _auditService.WriteLogAsync(
                        $"mcp.{_name}.executed",
                        _name,
                        auditResult,
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

            await _auditService.WriteLogAsync(
                $"mcp.{_name}.executed",
                _name,
                "success",
                $"Type: {tool.Type}",
                cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", ex.Message);
            var toolType = tool?.Type ?? "Unknown";
            var suggestedTool = string.Equals(toolType, "Query", StringComparison.OrdinalIgnoreCase) ? "execute_query_sql" : "execute_dml_sql";
            return $"Error: {ex.Message}\nerror definition: {finalDefinitionJson}\nplease fix the parameters or definition and use '{suggestedTool}' tools to try again.";
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
        var dryRunResult = await strategy.ExecuteDmlAsync(connectionString, dml, cancellationToken);
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
        return await strategy.ExecuteDmlAsync(connectionString, dml, cancellationToken);
    }

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

    private static string ReplaceParameters(string json, Dictionary<string, object> parameters)
    {
        if (parameters == null || parameters.Count == 0) return json;
        foreach (var param in parameters)
        {
            var key = System.Text.RegularExpressions.Regex.Escape(param.Key);

            // "{{key}}" — placeholder inside a JSON string (lookbehind/lookahead verify quotes)
            var innerPattern = @"\{\{\s*" + key + @"\s*\}\}";
            var quotedPattern = @"(?<="")" + innerPattern + @"(?="")";
            json = System.Text.RegularExpressions.Regex.Replace(json, quotedPattern, (param.Value?.ToString() ?? "null").Replace("\"", "\\\""));

            // {{key}} — bare placeholder: serialize as proper JSON token (type-aware)
            json = System.Text.RegularExpressions.Regex.Replace(json, innerPattern, System.Text.Json.JsonSerializer.Serialize(param.Value));
        }
        return json;
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
