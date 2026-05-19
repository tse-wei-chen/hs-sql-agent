using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    [McpServerTool, Description(@"
        Execute a complex SELECT query strictly using the structured definition block.

        CRITICAL RULES FOR LLM GENERATION:
        1. NO RAW MATH IN FIELDS: Never put arithmetic operators (+, -, *, /) inside a 'field' node's fieldName.
           - Incorrect: { ""type"": ""field"", ""fieldName"": ""price * quantity"" }
           - Correct: You MUST use ""type"": ""arithmetic"" and split it into left, operator, and right nodes.
        2. FUNCTION ARGUMENTS: Every argument inside a function's 'arguments' list MUST explicitly declare its 'type' (e.g., ""type"": ""field"", ""type"": ""constant"", ""type"": ""arithmetic"", ""type"": ""function""). Never omit the type discriminator.
        3. STRICT TABLE ALIASES: If you define an 'alias' for the main table (TableName) or a joined table, you MUST use exactly that prefix (e.g., 'p.product_name') for ALL fieldNames referencing that table across Select, Where, OrderBy, and GroupBy. Do not mix unaliased and aliased names.
        4. COLUMN REUSE: If a column has an 'alias' defined in SelectColumns, you CANNOT reuse that alias string inside WhereColumnsAndValues. You must reference the original column or expression.
        5. FROMQUERY SCOPE: When using 'FromQuery' (a subquery in FROM), the outer query's SelectColumns, WhereConditions, etc. can ONLY reference the subquery's output columns, NOT the inner tables or their aliases. For example, if the subquery produces columns 'customer_id' and 'order_count', the outer query must use 'customer_id' or 'order_count' — NOT 'o.order_id' (where 'o' is a join alias only inside the subquery).

        Supported SQL Capabilities: JOINs, WHERE filtering, GROUP BY, HAVING, ORDER BY, LIMIT/OFFSET, DISTINCT, CTEs (With clauses), Subqueries, and COMBINE (UNION/INTERSECT/EXCEPT).
    ")]
    public async Task<string> ExecuteQuerySafe(
        [Description("The structured complete query definition block. Ensure all polymorphic 'type' fields are strictly populated according to the schema rules.")]
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
            ValidateAllTableAccess(definition.TableName, definition.Joins, definition.CombineConditions, definition.CteConditions, definition.FromQuery, definition.SelectColumns, definition.WhereColumnsAndValues, definition.Alias);
            var result = await strategy.ExecuteQueryAsync(
                definition,
                sqlConfig.ConnectionString
            );

            await _auditService.WriteLogAsync(
                action: "mcp.query.executed",
                target: definition?.TableName ?? "unknown",
                result: "success",
                detail: $"Provider: {dbType}");

            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                action: "mcp.query.executed",
                target: definition?.TableName ?? "unknown",
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
                target: dml?.TableName ?? "unknown",
                result: "success",
                detail: $"Operation: {dml?.Operation}");

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
                tables = [.. tables.Where(t => whitelist.Contains($"{schemaName}.{t}"))];
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

    internal static void CollectFromQueryDefinition(QueryDefinition? qd, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (qd == null) return;
        if (!string.IsNullOrWhiteSpace(qd.Alias)) aliases.Add(qd.Alias);

        CollectReferencesAndAliases(
            qd.TableName, qd.Joins, qd.CombineConditions, qd.CteConditions,
            qd.FromQuery, qd.SelectColumns, qd.WhereColumnsAndValues,
            referenced, aliases);

        if (qd.HavingConditions != null)
            CollectFromHavingConditions(qd.HavingConditions, referenced, aliases);
        if (qd.OrderByColumns != null)
            CollectFromOrderByConditions(qd.OrderByColumns, referenced, aliases);
        if (qd.GroupByConditions != null)
            CollectFromGroupByConditions(qd.GroupByConditions, referenced, aliases);
    }

    internal static void CollectReferencesAndAliases(
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
                if (!string.IsNullOrWhiteSpace(c.CteAliasName)) aliases.Add(c.CteAliasName);
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
        {
            foreach (var s in selectColumns)
            {
                switch (s)
                {
                    case SubQuerySelectCondition sq:
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
                            break;
                        }
                    case FunctionSelectCondition funcSel:
                        CollectFromWheres(funcSel.FilterWhereConditions, referenced, aliases);
                        CollectFromFunctionArguments(funcSel.Arguments, referenced, aliases);
                        break;
                    case OperationSelectCondition opSel:
                        CollectFromSelectArithmeticCondition(opSel.Left, referenced, aliases);
                        CollectFromSelectArithmeticCondition(opSel.Right, referenced, aliases);
                        break;
                    case CaseWhenSelectCondition cwSel:
                        CollectFromCaseWhenClauses(cwSel.CaseWhen, referenced, aliases);
                        break;
                }
            }
        }

        if (whereConditions != null)
            CollectFromWheres(whereConditions, referenced, aliases);
    }

    internal static void CollectFromWheres(List<WhereCondition>? wheres, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (wheres == null) return;
        foreach (var w in wheres)
        {
            if (w is SubQueryWhereCondition subQueryWhere)
            {
                CollectFromQueryDefinition(subQueryWhere.SubQuery, referenced, aliases);
            }
            else if (w is GroupWhereCondition groupWhere)
            {
                CollectFromWheres(groupWhere.Groups, referenced, aliases);
            }
        }
    }

    internal static void CollectFromHavingConditions(List<HavingCondition>? havings, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (havings == null) return;
        foreach (var h in havings)
        {
            switch (h)
            {
                case FunctionHavingCondition funcHaving:
                    CollectFromSqlFunctionCondition(funcHaving.LeftFunction, referenced, aliases);
                    break;
                case GroupHavingCondition groupHaving:
                    CollectFromHavingConditions(groupHaving.Groups, referenced, aliases);
                    break;
            }
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
                CollectFromFunctionArguments(funcOrder.Arguments, referenced, aliases);
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
                CollectFromFunctionArguments(funcGroup.Arguments, referenced, aliases);
            }
        }
    }

    internal static void CollectFromWindowDefinition(WindowDefinition? window, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (window == null) return;
        CollectFromGroupByConditions(window.PartitionBy, referenced, aliases);
        CollectFromOrderByConditions(window.OrderBy, referenced, aliases);
    }

    internal static void CollectFromSelectArithmeticCondition(SelectArithmeticCondition? condition, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (condition == null) return;
        switch (condition)
        {
            case FunctionArithmeticCondition func:
                CollectFromWheres(func.FilterWhereConditions, referenced, aliases);
                CollectFromFunctionArguments(func.Arguments, referenced, aliases);
                break;
            case OperationArithmeticCondition op:
                CollectFromSelectArithmeticCondition(op.Left, referenced, aliases);
                CollectFromSelectArithmeticCondition(op.Right, referenced, aliases);
                break;
            case CaseWhenArithmeticCondition cw:
                CollectFromCaseWhenClauses(cw.CaseWhen, referenced, aliases);
                break;
        }
    }

    internal static void CollectFromCaseWhenClauses(List<CaseWhenClause>? clauses, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (clauses == null) return;
        foreach (var clause in clauses)
        {
            CollectFromWheres([clause.Condition], referenced, aliases);
        }
    }

    internal static void CollectFromFunctionArguments(List<SqlFunctionArgument>? args, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (args == null) return;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case NestedFunctionArgument nested:
                    CollectFromWheres(nested.FilterWhereConditions, referenced, aliases);
                    CollectFromFunctionArguments(nested.Arguments, referenced, aliases);
                    break;
                case ArithmeticFunctionArgument arith:
                    CollectFromSelectArithmeticCondition(arith.Left, referenced, aliases);
                    CollectFromSelectArithmeticCondition(arith.Right, referenced, aliases);
                    break;
            }
        }
    }

    internal static void CollectFromSqlFunctionCondition(SqlFunctionCondition? func, HashSet<string> referenced, HashSet<string> aliases)
    {
        if (func == null) return;
        CollectFromWheres(func.FilterWhereConditions, referenced, aliases);
        CollectFromFunctionArguments(func.Arguments, referenced, aliases);
        if (func.Window != null)
            CollectFromWindowDefinition(func.Window, referenced, aliases);
    }
}
