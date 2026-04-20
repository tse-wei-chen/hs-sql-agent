using System.Text.Json;
using Admin.Service.Interfaces;
using Common.Models;
using Admin.Service.Models;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;
using SqlAgent.Service.Enums;

namespace ToolBox.Tools;

public class CustomToolProxy(string name, ICustomSqlToolService customSqlToolService, IHttpContextAccessor httpContextAccessor, IConfiguration configuration, ISqlStrategyFactory sqlStrategyFactory)
{
    private readonly string _name = name;
    private readonly ICustomSqlToolService _customSqlToolService = customSqlToolService;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IConfiguration _configuration = configuration;
    private readonly ISqlStrategyFactory _sqlStrategyFactory = sqlStrategyFactory;

    public async Task<string> Execute(JsonElement arguments)
    {
        var parameters = new Dictionary<string, object>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
            {
                parameters[prop.Name] = prop.Value.ToString();
            }
        }

        var sqlConfig = ResolveSqlConfig();
        var tool = await _customSqlToolService.GetToolByNameAsync(_name);
        if (tool == null)
        {
            return $"Error: Tool '{_name}' not found.";
        }

        // 1. Validate SQL Config
        if (string.IsNullOrEmpty(sqlConfig.Provider) || string.IsNullOrEmpty(sqlConfig.ConnectionString))
        {
            return "Error: SQL configuration (provider/connection string) is missing.";
        }

        if (!Enum.TryParse<SqlAgentToolType>(sqlConfig.Provider, true, out var dbType))
        {
            return $"Error: Invalid SQL provider '{sqlConfig.Provider}'.";
        }

        // 2. Prepare Definition with parameter replacement
        string finalDefinitionJson = ReplaceParameters(tool.DefinitionJson, parameters);

        // 3. Execute based on type
        var strategy = _sqlStrategyFactory.GetStrategy(dbType);
        
        if (string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase))
        {
            var queryDef = JsonSerializer.Deserialize<QueryDefinition>(finalDefinitionJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (queryDef == null) return "Error: Failed to deserialize QueryDefinition.";

            return await strategy.ExecuteQueryAsync(
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
            var dmlDef = JsonSerializer.Deserialize<DmlDefinition>(finalDefinitionJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dmlDef == null) return "Error: Failed to deserialize DmlDefinition.";

            return await strategy.ExecuteDmlAsync(sqlConfig.ConnectionString, dmlDef);
        }

        return $"Error: Unsupported tool type '{tool.Type}'.";
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

    private string ReplaceParameters(string json, Dictionary<string, object> parameters)
    {
        if (parameters == null || parameters.Count == 0) return json;

        foreach (var param in parameters)
        {
            var token = "{{" + param.Key + "}}";
            var valueStr = param.Value?.ToString() ?? "null";
            json = json.Replace(token, valueStr);
        }

        return json;
    }
}
