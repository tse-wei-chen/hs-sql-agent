using System.ComponentModel;
using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using ModelContextProtocol.Server;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;

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
        [Description("Top-level alias for the source table or subquery. (Optional)")]
        string? alias = null)
    {
        var sqlConfig = await ResolveSqlConfigAsync();
        if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
        {
            return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
        }
        var strategy = _sqlStrategyFactory.GetStrategy(dbType);
        try
        {
            ValidateToolAccess("execute_query_safe");
            ValidateAllTableAccess(tableName, joins, combineConditions, cteConditions, fromQuery, selectColumns, whereConditions, alias);
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
            return "Execution failed: " + ex.Message;
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
            ValidateAllTableAccess(dml?.TableName, null, null, null, dml?.FromQuery, null, dml?.WhereConditions);
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
            ValidateAllTableAccess($"{schemaName}.{tableName}", null, null, null, null, null, null);
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

            // Apply table whitelist filter
            var whitelist = ResolveTableWhitelist();
            if (whitelist is { Count: > 0 })
            {
                tables = tables.Where(t => whitelist.Contains($"{schemaName}.{t}")).ToList();
            }

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

    private void ValidateAllTableAccess(
        string? tableName,
        List<JoinCondition>? joins,
        List<CombineCondition>? combineConditions,
        List<CteCondition>? cteConditions,
        QueryDefinition? fromQuery,
        List<SelectCondition>? selectColumns,
        List<WhereCondition>? whereConditions,
        string? topLevelAlias = null)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(topLevelAlias)) aliases.Add(topLevelAlias);

        CollectReferencesAndAliases(
            tableName, joins, combineConditions, cteConditions,
            fromQuery, selectColumns, whereConditions,
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

    private static void CollectFromQueryDefinition(QueryDefinition? qd, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (qd == null) return;
        if (!string.IsNullOrWhiteSpace(qd.Alias)) aliases.Add(qd.Alias);

        CollectReferencesAndAliases(
            qd.TableName, qd.Joins, qd.CombineConditions, qd.CteConditions,
            qd.FromQuery, qd.SelectColumns, qd.WhereColumnsAndValues,
            referenced, aliases);
    }

    private static void CollectReferencesAndAliases(
        string? tableName,
        List<JoinCondition>? joins,
        List<CombineCondition>? combineConditions,
        List<CteCondition>? cteConditions,
        QueryDefinition? fromQuery,
        List<SelectCondition>? selectColumns,
        List<WhereCondition>? whereConditions,
        HashSet<string> referenced,
        HashSet<string> aliases)
    {
        if (!string.IsNullOrWhiteSpace(tableName)) referenced.Add(tableName);

        if (cteConditions != null)
        {
            foreach (var c in cteConditions)
            {
                if (!string.IsNullOrWhiteSpace(c.Name)) aliases.Add(c.Name);
                CollectFromQueryDefinition(c.Query, referenced, aliases);
            }
        }

        if (joins != null)
        {
            foreach (var j in joins)
            {
                if (!string.IsNullOrWhiteSpace(j.Table)) referenced.Add(j.Table);
                if (!string.IsNullOrWhiteSpace(j.Alias)) aliases.Add(j.Alias);
                CollectFromQueryDefinition(j.SubQuery, referenced, aliases);
            }
        }

        if (fromQuery != null) CollectFromQueryDefinition(fromQuery, referenced, aliases);

        if (combineConditions != null)
            foreach (var c in combineConditions) CollectFromQueryDefinition(c.Query, referenced, aliases);

        if (selectColumns != null)
            foreach (var s in selectColumns) CollectFromQueryDefinition(s.SubQuery, referenced, aliases);

        if (whereConditions != null)
            CollectFromWheres(whereConditions, referenced, aliases);
    }

    private static void CollectFromWheres(List<WhereCondition>? wheres, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (wheres == null) return;
        foreach (var w in wheres)
        {
            CollectFromQueryDefinition(w.SubQuery, referenced, aliases);
            if (w.Groups != null) CollectFromWheres(w.Groups, referenced, aliases);
        }
    }
}
