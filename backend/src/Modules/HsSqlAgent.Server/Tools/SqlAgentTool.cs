using System.ComponentModel;
using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Models;
using ModelContextProtocol.Server;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;

namespace HsSqlAgent.Server.Tools;

[McpServerToolType]
public class SqlAgentTool(IConfiguration configuration, IHttpContextAccessor httpContextAccessor, ISqlStrategyFactory sqlStrategyFactory, IAuditService auditService, IDbSemanticService semanticService)
{
    private readonly IConfiguration _configuration = configuration;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlStrategyFactory _sqlStrategyFactory = sqlStrategyFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IDbSemanticService _semanticService = semanticService;

    [McpServerTool, Description(@"
        Execute a complex SELECT query strictly using the structured definition block.

        CRITICAL RULES FOR LLM GENERATION:
        1. NO RAW MATH IN FIELDS: Never put arithmetic operators (+, -, *, /) inside a 'field' node's fieldName.
        2. FUNCTION ARGUMENTS: Every argument inside a function's 'arguments' list MUST explicitly declare its 'type'.
        3. STRICT TABLE ALIASES: If you define an 'alias', use it consistently.
        4. COLUMN REUSE: An aliased column cannot be referenced by its alias in WHERE.
        5. FROMQUERY SCOPE: Outer query can only reference subquery output columns.

        Supported: JOINs, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT/OFFSET, DISTINCT, CTEs, Subqueries, COMBINE.
    ")]
    public async Task<string> ExecuteQuerySafe(
        [Description("The structured complete query definition block.")]
        QueryDefinition definition)
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
            if (definition == null)
                return "Error: Query definition is missing.";

            ValidateAllTableAccess(definition.TableName, definition.Joins, definition.CombineConditions, definition.CteConditions, definition.FromQuery, definition.SelectColumns, definition.WhereColumnsAndValues, definition.Alias);
            var result = await strategy.ExecuteQueryAsync(definition, sqlConfig.ConnectionString);

            await _auditService.WriteLogAsync("mcp.query.executed", definition.TableName ?? "unknown", "success", $"Provider: {dbType}");
            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync("mcp.query.executed", definition?.TableName ?? "unknown", "failed", ex.Message);
            return "Execution failed: " + ex.Message;
        }
    }

    [McpServerTool, Description(@"
        Execute a DML operation (INSERT, UPDATE, DELETE).
        Two-step safety: first call (without ConfirmToken) does dry-run; second call (with ConfirmToken) commits.
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

            await _auditService.WriteLogAsync("mcp.dml.executed", dml?.TableName ?? "unknown", "success", $"Operation: {dml?.Operation}");
            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync("mcp.dml.executed", dml?.TableName ?? "unknown", "failed", ex.Message);
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
                return "Table name cannot be empty.";
            }
            var strategy = _sqlStrategyFactory.GetStrategy(dbType);
            var columns = await strategy.GetColumnsAsync(sqlConfig.ConnectionString, schemaName, tableName);

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
                            col.Description = string.Join(". ", parts);
                    }
                }
            }

            await _auditService.WriteLogAsync("mcp.get_columns", $"{schemaName}.{tableName}", "success");
            return JsonSerializer.Serialize(columns);
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync("mcp.get_columns", $"{schemaName}.{tableName}", "failed", ex.Message);
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

            await _auditService.WriteLogAsync("mcp.get_schemas", "database", "success");
            return string.Join(", ", schemas);
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync("mcp.get_schemas", "database", "failed", ex.Message);
            return $"Error getting schemas: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get list of tables in a schema.")]
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

            var whitelist = ResolveTableWhitelist();
            if (whitelist is { Count: > 0 })
            {
                tables = [.. tables.Where(t => whitelist.Contains($"{schemaName}.{t}"))];
            }

            var dbId = ResolveDbManagementId();
            if (dbId.HasValue)
            {
                var semantics = await _semanticService.GetSemanticsByDbIdAsync(dbId.Value);
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

                await _auditService.WriteLogAsync("mcp.get_tables", schemaName, "success");
                return string.Join(", ", tablesWithDesc);
            }

            await _auditService.WriteLogAsync("mcp.get_tables", schemaName, "success");
            return string.Join(", ", tables);
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync("mcp.get_tables", schemaName, "failed", ex.Message);
            return $"Error getting tables: {ex.Message}";
        }
    }

    [McpServerTool, Description(@"
        Update the semantic layer metadata for tables and columns.
        Enriches schema discovery with human-readable names and descriptions.
    ")]
    public async Task<string> UpdateSemanticLayer(
        [Description("List of semantic entries to upsert.")]
        List<SemanticLayerEntry> entries)
    {
        try
        {
            ValidateToolAccess("update_semantic_layer");
            var dbId = ResolveDbManagementId();
            if (!dbId.HasValue)
                return "Error: No database connection associated with this API key.";
            if (entries == null || entries.Count == 0)
                return "Error: No semantic entries provided.";

            var results = new List<string>();
            foreach (var entry in entries)
            {
                var request = new DbSemanticRequest
                {
                    DbManagementId = dbId.Value,
                    SchemaName = entry.SchemaName,
                    TableName = entry.TableName,
                    ColumnName = entry.ColumnName,
                    Description = entry.Description,
                    DisplayName = entry.DisplayName
                };
                await _semanticService.UpsertSemanticAsync(request);
                var target = string.IsNullOrEmpty(entry.ColumnName)
                    ? $"{entry.SchemaName ?? "dbo"}.{entry.TableName}"
                    : $"{entry.SchemaName ?? "dbo"}.{entry.TableName}.{entry.ColumnName}";
                results.Add($"  - {target}: updated");
            }

            await _auditService.WriteLogAsync("mcp.update_semantic_layer", $"updated {entries.Count} entries", "success");
            return $"Semantic layer updated successfully:\n{string.Join("\n", results)}";
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync("mcp.update_semantic_layer", "semantic_layer", "failed", ex.Message);
            return $"Error updating semantic layer: {ex.Message}";
        }
    }

    private static bool CheckProviderAndConnectionString(SqlRuntimeConfig sqlConfig, out SqlAgentToolType dbType)
    {
        dbType = default;
        if (string.IsNullOrEmpty(sqlConfig.Provider) || string.IsNullOrEmpty(sqlConfig.ConnectionString))
            return false;
        return Enum.TryParse(sqlConfig.Provider, true, out dbType);
    }

    private async Task<SqlRuntimeConfig> ResolveSqlConfigAsync()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is not null)
        {
            var provider = context.Items[Common.Models.McpContextItemKeys.SqlProvider]?.ToString();
            var connectionString = context.Items[Common.Models.McpContextItemKeys.SqlConnectionString]?.ToString();
            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(connectionString))
                return new SqlRuntimeConfig { Provider = provider, ConnectionString = connectionString };
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
        var allowedTools = context.Items[Common.Models.McpContextItemKeys.AllowedTools] as string;
        if (string.IsNullOrWhiteSpace(allowedTools)) return;
        var isAllowed = allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (!isAllowed)
            throw new UnauthorizedAccessException($"API key does not have permission to use tool: {toolName}");
    }

    private int? ResolveDbManagementId()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context != null && context.Items.TryGetValue(Common.Models.McpContextItemKeys.DbManagementId, out var idObj) && idObj is int id)
            return id;
        return null;
    }

    private HashSet<string>? ResolveTableWhitelist()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return null;
        var tableWhitelist = context.Items[Common.Models.McpContextItemKeys.TableWhitelist] as string;
        if (string.IsNullOrWhiteSpace(tableWhitelist)) return null;
        return tableWhitelist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
        CollectReferencesAndAliases(tableName, joins, combineConditions, cteConditions, fromQuery, selectColumns, whereConditions, referenced, aliases);
        var violations = referenced.Where(t => !aliases.Contains(t)).Where(t => !whitelist.Contains(t)).ToList();
        if (violations.Count > 0)
            throw new UnauthorizedAccessException($"API key does not have permission to access table(s): {string.Join(", ", violations)}");
    }

    internal static void CollectFromQueryDefinition(QueryDefinition? qd, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (qd == null) return;
        if (!string.IsNullOrWhiteSpace(qd.Alias)) aliases.Add(qd.Alias);
        CollectReferencesAndAliases(qd.TableName, qd.Joins, qd.CombineConditions, qd.CteConditions, qd.FromQuery, qd.SelectColumns, qd.WhereColumnsAndValues, referenced, aliases);
        if (qd.HavingConditions != null) CollectFromHavingConditions(qd.HavingConditions, referenced, aliases);
        if (qd.OrderByColumns != null) CollectFromOrderByConditions(qd.OrderByColumns, referenced, aliases);
        if (qd.GroupByConditions != null) CollectFromGroupByConditions(qd.GroupByConditions, referenced, aliases);
    }

    internal static void CollectReferencesAndAliases(
        string? tableName, List<JoinCondition>? joins, List<CombineCondition>? combineConditions,
        List<CteCondition>? cteConditions, QueryDefinition? fromQuery,
        List<SelectCondition>? selectColumns, List<WhereCondition>? whereConditions,
        HashSet<string> referenced, HashSet<string> aliases)
    {
        if (!string.IsNullOrWhiteSpace(tableName)) referenced.Add(tableName);
        if (cteConditions != null)
            foreach (var c in cteConditions)
            {
                if (!string.IsNullOrWhiteSpace(c.CteAliasName)) aliases.Add(c.CteAliasName);
                CollectFromQueryDefinition(c.Query, referenced, aliases);
            }
        if (joins != null)
            foreach (var j in joins)
            {
                if (!string.IsNullOrWhiteSpace(j.Table)) referenced.Add(j.Table);
                if (!string.IsNullOrWhiteSpace(j.Alias)) aliases.Add(j.Alias);
                CollectFromQueryDefinition(j.SubQuery, referenced, aliases);
            }
        if (fromQuery != null) CollectFromQueryDefinition(fromQuery, referenced, aliases);
        if (combineConditions != null)
            foreach (var c in combineConditions) CollectFromQueryDefinition(c.Query, referenced, aliases);
        if (selectColumns != null)
        {
            foreach (var s in selectColumns)
            {
                if (s is SubQuerySelectCondition sq)
                {
                    var qd = new QueryDefinition
                    {
                        TableName = sq.TableName,
                        FromQuery = sq.FromQuery,
                        Alias = sq.Alias,
                        Distinct = sq.Distinct,
                        SelectColumns = sq.SelectColumns,
                        WhereColumnsAndValues = sq.WhereColumnsAndValues,
                        OrderByColumns = sq.OrderByColumns,
                        GroupByConditions = sq.GroupByConditions,
                        HavingConditions = sq.HavingConditions,
                        Joins = sq.Joins,
                        CombineConditions = sq.CombineConditions,
                        CteConditions = sq.CteConditions,
                        Limit = sq.Limit,
                        Offset = sq.Offset
                    };
                    CollectFromQueryDefinition(qd, referenced, aliases);
                }
                else if (s is FunctionSelectCondition funcSel)
                {
                    CollectFromWheres(funcSel.FilterWhereConditions, referenced, aliases);
                    CollectFromExpressions(funcSel.Arguments, referenced, aliases);
                }
                else if (s is OperationSelectCondition opSel)
                {
                    CollectFromExpression(opSel.Left, referenced, aliases);
                    CollectFromExpression(opSel.Right, referenced, aliases);
                }
                else if (s is CaseWhenSelectCondition cwSel)
                    CollectFromCaseWhenClauses(cwSel.CaseWhen, referenced, aliases);
            }
        }
        if (whereConditions != null) CollectFromWheres(whereConditions, referenced, aliases);
    }

    internal static void CollectFromWheres(List<WhereCondition>? wheres, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (wheres == null) return;
        foreach (var w in wheres)
        {
            if (w is SubQueryWhereCondition subQueryWhere)
                CollectFromQueryDefinition(subQueryWhere.SubQuery, referenced, aliases);
            else if (w is GroupWhereCondition groupWhere)
                CollectFromWheres(groupWhere.Groups, referenced, aliases);
        }
    }

    internal static void CollectFromHavingConditions(List<HavingCondition>? havings, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (havings == null) return;
        foreach (var h in havings)
        {
            if (h is FunctionHavingCondition funcHaving)
                CollectFromSqlFunctionCondition(funcHaving.LeftFunction, referenced, aliases);
            else if (h is GroupHavingCondition groupHaving)
                CollectFromHavingConditions(groupHaving.Groups, referenced, aliases);
        }
    }

    internal static void CollectFromOrderByConditions(List<OrderByCondition>? orderBys, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (orderBys == null) return;
        foreach (var o in orderBys)
        {
            if (o is FunctionOrderByCondition funcOrder)
            {
                CollectFromWheres(funcOrder.FilterWhereConditions, referenced, aliases);
                CollectFromExpressions(funcOrder.Arguments, referenced, aliases);
            }
        }
    }

    internal static void CollectFromGroupByConditions(List<GroupByCondition>? groupBys, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (groupBys == null) return;
        foreach (var g in groupBys)
        {
            if (g is FunctionGroupByCondition funcGroup)
            {
                CollectFromWheres(funcGroup.FilterWhereConditions, referenced, aliases);
                CollectFromExpressions(funcGroup.Arguments, referenced, aliases);
            }
        }
    }

    internal static void CollectFromWindowDefinition(WindowDefinition? window, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (window == null) return;
        CollectFromGroupByConditions(window.PartitionBy, referenced, aliases);
        CollectFromOrderByConditions(window.OrderBy, referenced, aliases);
    }

    internal static void CollectFromExpression(SelectCondition? condition, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (condition == null) return;
        if (condition is FunctionSelectCondition func)
        {
            CollectFromWheres(func.FilterWhereConditions, referenced, aliases);
            CollectFromExpressions(func.Arguments, referenced, aliases);
        }
        else if (condition is OperationSelectCondition op)
        {
            CollectFromExpression(op.Left, referenced, aliases);
            CollectFromExpression(op.Right, referenced, aliases);
        }
        else if (condition is CaseWhenSelectCondition cw)
            CollectFromCaseWhenClauses(cw.CaseWhen, referenced, aliases);
    }

    internal static void CollectFromExpressions(List<SelectCondition>? args, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (args == null) return;
        foreach (var arg in args) CollectFromExpression(arg, referenced, aliases);
    }

    internal static void CollectFromCaseWhenClauses(List<CaseWhenClause>? clauses, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (clauses == null) return;
        foreach (var clause in clauses) CollectFromWheres([clause.Condition], referenced, aliases);
    }

    internal static void CollectFromSqlFunctionCondition(SqlFunctionCondition? func, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (func == null) return;
        CollectFromWheres(func.FilterWhereConditions, referenced, aliases);
        CollectFromExpressions(func.Arguments, referenced, aliases);
        if (func.Window != null) CollectFromWindowDefinition(func.Window, referenced, aliases);
    }
}
