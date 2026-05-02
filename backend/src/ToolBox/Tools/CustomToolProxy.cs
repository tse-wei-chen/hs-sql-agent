using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
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
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

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
        try
        {
            var sqlConfig = ResolveSqlConfig();
            var tool = await _customSqlToolService.GetToolByNameAsync(_name);
            if (tool == null)
            {
                var error = $"Error: Tool '{_name}' not found.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            // 1. Validate SQL Config
            if (string.IsNullOrEmpty(sqlConfig.Provider) || string.IsNullOrEmpty(sqlConfig.ConnectionString))
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
            string finalDefinitionJson = ReplaceParameters(tool.DefinitionJson, parameters);
            Console.WriteLine($"Final JSON: {finalDefinitionJson}\n");
            // 3. Execute based on type
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);

            string result;
            if (string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase))
            {
                var queryDef = JsonSerializer.Deserialize<QueryDefinition>(finalDefinitionJson, _jsonOptions);
                if (queryDef == null)
                {
                    result = "Error: Failed to deserialize QueryDefinition.";
                    await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                    return result;
                }

                result = await strategy.ExecuteQueryAsync(
                    sqlConfig.ConnectionString,
                    queryDef.TableName,
                    queryDef.SelectColumns,
                    queryDef.WhereColumnsAndValues,
                    queryDef.OrderByColumns,
                    queryDef.GroupByConditions,
                    queryDef.HavingConditions,
                    queryDef.CombineConditions,
                    queryDef.CteConditions,
                    queryDef.Limit,
                    queryDef.Offset,
                    queryDef.Joins,
                    queryDef.FromQuery,
                    queryDef.Alias,
                    queryDef.Distinct
                );
            }
            else if (string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase))
            {
                var dmlDef = JsonSerializer.Deserialize<DmlDefinition>(finalDefinitionJson, _jsonOptions);
                if (dmlDef == null)
                {
                    result = "Error: Failed to deserialize DmlDefinition.";
                    await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                    return result;
                }

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
            throw;
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
}
