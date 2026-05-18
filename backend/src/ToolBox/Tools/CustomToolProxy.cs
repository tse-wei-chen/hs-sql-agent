using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using ModelContextProtocol;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;

namespace ToolBox.Tools;

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

    public async Task<string> Execute(JsonElement arguments)
    {
        var parameters = new Dictionary<string, object>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
            {
                parameters[prop.Name] = _queryValueParserService.UnwrapJsonElement(prop.Value);
            }
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

            // 1. Validate SQL Config
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

            // 2. Prepare Definition with parameter replacement
            finalDefinitionJson = ReplaceParameters(tool.DefinitionJson, parameters);
            Console.WriteLine($"Final JSON: {finalDefinitionJson}\n");
            // 3. Execute based on type
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

                result = await strategy.ExecuteQueryAsync(
                    queryDef,
                    sqlConfig.ConnectionString
                );
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

                result = await strategy.ExecuteDmlAsync(sqlConfig.ConnectionString, dmlDef);
            }
            else
            {
                result = $"Error: Unsupported tool type '{tool.Type}'.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                return result;
            }

            await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "success", $"Type: {tool.Type}");
            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", ex.Message);

            var toolType = tool?.Type ?? "Unknown";
            bool isQueryError = string.Equals(toolType, "Query", StringComparison.OrdinalIgnoreCase);
            var suggestedTool = isQueryError ? "execute_query_safe" : "execute_dml_safe";

            return $"Error: {ex.Message}\n" +
                   $"error definition: {finalDefinitionJson}\n" +
                   $"please fix the parameters or definition and use '{suggestedTool}' tools to try again.";
        }
    }

    private SqlRuntimeConfig ResolveSqlConfig()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var provider = httpContext.Items[McpContextItemKeys.SqlProvider]?.ToString();
            var connectionString = httpContext.Items[McpContextItemKeys.SqlConnectionString]?.ToString();

            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(connectionString))
            {
                return new SqlRuntimeConfig { Provider = provider, ConnectionString = connectionString };
            }
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
            var pattern = $@"\{{\{{\s*{System.Text.RegularExpressions.Regex.Escape(param.Key)}\s*\}}\}}";
            var valueStr = param.Value?.ToString() ?? "null";
            var sanitizedValue = valueStr.Replace("\"", "\\\"");

            json = System.Text.RegularExpressions.Regex.Replace(json, pattern, sanitizedValue);
        }

        return json;
    }

    private HashSet<string>? ResolveTableWhitelist()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return null;

        var tableWhitelist = context.Items[McpContextItemKeys.TableWhitelist] as string;
        if (string.IsNullOrWhiteSpace(tableWhitelist)) return null;

        return tableWhitelist
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ValidateAllTableAccess(QueryDefinition queryDef)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(queryDef.Alias)) aliases.Add(queryDef.Alias);

        SqlAgentTool.CollectReferencesAndAliases(
            queryDef.TableName, queryDef.Joins, queryDef.CombineConditions, queryDef.CteConditions,
            queryDef.FromQuery, queryDef.SelectColumns, queryDef.WhereColumnsAndValues,
            referenced, aliases);

        SqlAgentTool.CollectFromHavingConditions(queryDef.HavingConditions, referenced, aliases);
        SqlAgentTool.CollectFromOrderByConditions(queryDef.OrderByColumns, referenced, aliases);
        SqlAgentTool.CollectFromGroupByConditions(queryDef.GroupByConditions, referenced, aliases);

        var violations = referenced
            .Where(t => !aliases.Contains(t))
            .Where(t => !whitelist.Contains(t))
            .ToList();

        if (violations.Count > 0)
        {
            throw new UnauthorizedAccessException(
                $"API key does not have permission to access table(s): {string.Join(", ", violations)}");
        }
    }

    private void ValidateAllTableAccess(DmlDefinition dmlDef)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        SqlAgentTool.CollectReferencesAndAliases(
            dmlDef.TableName, null, null, null,
            dmlDef.FromQuery, null, dmlDef.WhereConditions,
            referenced, aliases);

        var violations = referenced
            .Where(t => !aliases.Contains(t))
            .Where(t => !whitelist.Contains(t))
            .ToList();

        if (violations.Count > 0)
        {
            throw new UnauthorizedAccessException(
                $"API key does not have permission to access table(s): {string.Join(", ", violations)}");
        }
    }
}
