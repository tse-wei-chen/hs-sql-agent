using System.ComponentModel;
using ModelContextProtocol.Server;
using Admin.Service.Models;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;
using System.Text.Json;
using Common.Models;
using Admin.Service.Interfaces;

namespace ToolBox.Tools;

[McpServerToolType]
public class SqlAgentTool(IConfiguration configuration, IHttpContextAccessor httpContextAccessor, ISqlStrategyFactory sqlStrategyFactory, IAuditService auditService, IDbSemanticService semanticService)
{
    private readonly IConfiguration _configuration = configuration;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlStrategyFactory _sqlStrategyFactory = sqlStrategyFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IDbSemanticService _semanticService = semanticService;

    [McpServerTool, Description("Execute a query (supports join, where, group, having, combine, cte, order by, limit, offset, distinct, subqueries).")]
    public async Task<string> ExecuteQuerySafe(
        [Description("The table name (use schema-qualified table name). Can be null if fromQuery is provided.")]
        string? tableName = null,
        [Description("List of columns to select")]
        List<SelectCondition>? selectColumns = null,
        [Description("List of where conditions")]
        List<WhereCondition>? whereConditions = null,
        [Description("List of columns to order by. Each item can include 'Field', 'Aggregation' (e.g., COUNT, SUM), and 'Direction' (ASC or DESC).")]
        List<OrderByCondition>? orderByColumns = null,
        [Description("Limit the number of results returned")]
        int limit = 0,
        [Description("Offset the number of results returned")]
        int offset = 0,
        [Description("List of joins. Each join is a dictionary with keys: 'Table', 'On', and optional 'Type' (default 'INNER').")]
        List<JoinCondition>? joins = null,
        [Description("List of group by conditions. Each condition includes 'Table', 'Field'.")]
        List<GroupByCondition>? groupByConditions = null,
        [Description("List of having conditions.")]
        List<HavingCondition>? havingConditions = null,
        [Description("List of combine conditions (union/union all/intersect/except).")]
        List<CombineCondition>? combineConditions = null,
        [Description("List of CTE definitions.")]
        List<CteCondition>? cteConditions = null,
        [Description("Whether to use SELECT DISTINCT")]
        bool distinct = false,
        [Description("Source subquery definition. If provided, tableName is ignored.")]
        QueryDefinition? fromQuery = null,
        [Description("Alias for the source table or subquery (Optional).")]
        string? alias = null)
    {
        ValidateToolAccess("execute_query_safe");
        var sqlConfig = await ResolveSqlConfigAsync();
        if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
        {
            return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
        }
        var strategy = _sqlStrategyFactory.GetStrategy(dbType);
        try
        {
            var result = await strategy.ExecuteQueryAsync(
                connectionString: sqlConfig.ConnectionString,
                tableName: tableName,
                selectColumns: selectColumns,
                whereConditions: whereConditions,
                orderByColumns: orderByColumns,
                groupByConditions: groupByConditions,
                havingConditions: havingConditions,
                combineConditions: combineConditions,
                cteConditions: cteConditions,
                limit: limit > 0 ? limit : null,
                offset: offset > 0 ? offset : null,
                joins: joins,
                fromQuery: fromQuery,
                alias: alias,
                distinct: distinct
            );

            await _auditService.WriteLogAsync(
                action: "mcp.query.executed",
                target: tableName ?? "subquery",
                result: "success",
                detail: $"Provider: {dbType}");

            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                action: "mcp.query.executed",
                target: tableName ?? "subquery",
                result: "failed",
                detail: ex.Message);
            throw;
        }
    }

    [McpServerTool, Description(@"
		Execute a DML operation (INSERT, UPDATE, DELETE). 
		This tool uses a mandatory two-step safety mechanism:
		1. First call (without ConfirmToken): Performs a Dry Run, returns affected rows and a unique ConfirmToken.
		2. Second call (with ConfirmToken): Actually commits the operation if the token matches.
	")]
    public async Task<string> ExecuteDmlSafe(
        [Description("The DML definition (operation, table, values, conditions).")]
        DmlDefinition dml)
    {
        try
        {
            ValidateToolAccess("execute_dml_safe");
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            {
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
            }
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);
            var result = await strategy.ExecuteDmlAsync(sqlConfig.ConnectionString, dml);

            await _auditService.WriteLogAsync(
                action: "mcp.dml.executed",
                target: dml.TableName,
                result: "success",
                detail: $"Operation: {dml.Operation}");

            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                action: "mcp.dml.executed",
                target: dml?.TableName ?? "unknown",
                result: "failed",
                detail: ex.Message);
            return $"Error executing DML: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get column names and types of a table.")]
    public async Task<string> GetColumns([Description("The schema name")] string schemaName, [Description("The table name")] string tableName)
    {
        try
        {
            ValidateToolAccess("get_columns");
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            {
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
            }
            if (string.IsNullOrEmpty(tableName))
            {
                return "Table name cannot be empty. please provide a valid table name.";
            }
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);
            var columns = await strategy.GetColumnsAsync(sqlConfig.ConnectionString, schemaName, tableName);

            // Merge Semantic Data
            var dbId = ResolveDbManagementId();
            if (dbId.HasValue)
            {
                var semantics = await _semanticService.GetSemanticsByDbIdAsync(dbId.Value);
                var tableSemantics = semantics.Where(s =>
                    string.Equals(s.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.TableName, tableName, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var col in columns)
                {
                    var semantic = tableSemantics.FirstOrDefault(s => string.Equals(s.ColumnName, col.Name, StringComparison.OrdinalIgnoreCase));
                    if (semantic != null)
                    {
                        var parts = new List<string>();
                        if (!string.IsNullOrWhiteSpace(semantic.DisplayName)) parts.Add($"Display Name: {semantic.DisplayName}");
                        if (!string.IsNullOrWhiteSpace(semantic.Description)) parts.Add(semantic.Description);
                        
                        if (parts.Count > 0)
                        {
                            col.Description = string.Join(". ", parts);
                        }
                    }
                }
            }

            await _auditService.WriteLogAsync(
                action: "mcp.get_columns",
                target: $"{schemaName}.{tableName}",
                result: "success");

            return JsonSerializer.Serialize(columns);
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                action: "mcp.get_columns",
                target: $"{schemaName}.{tableName}",
                result: "failed",
                detail: ex.Message);
            return $"Error getting columns: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get list of schemas in the database.")]
    public async Task<string> GetSchemas()
    {
        try
        {
            ValidateToolAccess("get_schemas");
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            {
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
            }
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);
            var schemas = await strategy.GetSchemasAsync(sqlConfig.ConnectionString);

            await _auditService.WriteLogAsync(
                action: "mcp.get_schemas",
                target: "database",
                result: "success");

            return string.Join(", ", schemas);
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                action: "mcp.get_schemas",
                target: "database",
                result: "failed",
                detail: ex.Message);
            return $"Error getting schemas: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get list of tables in a schema. ")]
    public async Task<string> GetTables([Description("The schema name")] string schemaName)
    {
        try
        {
            ValidateToolAccess("get_tables");
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            {
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
            }
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);
            var tables = await strategy.GetTablesAsync(sqlConfig.ConnectionString, schemaName);

            // Merge Semantic Data
            var dbId = ResolveDbManagementId();
            if (dbId.HasValue)
            {
                var semantics = await _semanticService.GetSemanticsByDbIdAsync(dbId.Value);
                
                // To keep it simple and compatible with LLM expectations of "list of tables", 
                // I'll append descriptions in parentheses if they exist.
                var tablesWithDesc = tables.Select(t =>
                {
                    var s = semantics.FirstOrDefault(item =>
                            string.Equals(item.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.TableName, t, StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(item.ColumnName));
                    
                    if (s == null) return t;
                    
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(s.DisplayName)) parts.Add($"Display Name: {s.DisplayName}");
                    if (!string.IsNullOrWhiteSpace(s.Description)) parts.Add(s.Description);
                    
                    return parts.Count > 0 ? $"{t} ({string.Join(". ", parts)})" : t;
                });

                await _auditService.WriteLogAsync(
                    action: "mcp.get_tables",
                    target: schemaName,
                    result: "success");

                return string.Join(", ", tablesWithDesc);
            }

            await _auditService.WriteLogAsync(
                action: "mcp.get_tables",
                target: schemaName,
                result: "success");

            return string.Join(", ", tables);
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                action: "mcp.get_tables",
                target: schemaName,
                result: "failed",
                detail: ex.Message);
            return $"Error getting tables: {ex.Message}";
        }
    }

    private static bool CheckProviderAndConnectionString(SqlRuntimeConfig sqlConfig, out SqlAgentToolType dbType)
    {
        dbType = default;
        if (string.IsNullOrEmpty(sqlConfig.Provider))
        {
            return false;
        }
        if (string.IsNullOrEmpty(sqlConfig.ConnectionString))
        {
            return false;
        }
        if (!Enum.TryParse(sqlConfig.Provider, true, out dbType))
        {
            return false;
        }
        return true;
    }

    private async Task<SqlRuntimeConfig> ResolveSqlConfigAsync()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is not null)
        {
            var provider = context.Items[McpContextItemKeys.SqlProvider]?.ToString();
            var connectionString = context.Items[McpContextItemKeys.SqlConnectionString]?.ToString();

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

    private void ValidateToolAccess(string? toolName = null)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return;

        var allowedTools = context.Items[McpContextItemKeys.AllowedTools] as string;

        if (string.IsNullOrWhiteSpace(allowedTools))
        {
            return;
        }
        var isAllowed = allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase));

        if (!isAllowed)
        {
            throw new UnauthorizedAccessException($"API key does not have permission to use tool: {toolName}");
        }
    }

    private int? ResolveDbManagementId()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context != null && context.Items.TryGetValue(McpContextItemKeys.DbManagementId, out var idObj) && idObj is int id)
        {
            return id;
        }
        return null;
    }
}
