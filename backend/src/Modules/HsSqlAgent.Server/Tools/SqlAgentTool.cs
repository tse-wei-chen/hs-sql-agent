using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static ModelContextProtocol.Protocol.ElicitRequestParams;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Validation;

namespace HsSqlAgent.Server.Tools;

[McpServerToolType]
public partial class SqlAgentTool(
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor,
    ISqlStrategyFactory sqlStrategyFactory,
    IAuditService auditService,
    IDbSemanticService semanticService,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter sqlConcurrencyLimiter)
{
    private readonly IConfiguration _configuration = configuration;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlStrategyFactory _sqlStrategyFactory = sqlStrategyFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IDbSemanticService _semanticService = semanticService;
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly ISqlExecutionConcurrencyLimiter _sqlConcurrencyLimiter = sqlConcurrencyLimiter;

    [McpServerTool, Description(@"
        Execute one SELECT SQL statement. The server parses SQL into a QueryDefinition, validates it,
        applies table whitelist checks, then executes it through the configured SQL strategy.

        Use get_schemas/get_tables/get_columns first when you need database structure. Only send a single SELECT statement.
        Supported SQL includes JOINs, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT/OFFSET, DISTINCT, CTEs, subqueries, and UNION/INTERSECT/EXCEPT.
    ")]
    public async Task<string> ExecuteQuerySql(
        [Description("A single SELECT SQL statement to parse, validate, and execute.")]
        string sql)
    {
        var stopwatch = Stopwatch.StartNew();
        var sqlConfig = await ResolveSqlConfigAsync();
        if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
        {
            return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
        }
        var strategy = _sqlStrategyFactory.GetStrategy(dbType);
        QueryDefinition? definition = null;
        try
        {
            ValidateToolAccess("execute_query_sql");
            if (string.IsNullOrWhiteSpace(sql))
                return "Error: SQL is missing.";

            sql = NormalizeSql(sql);
            definition = SqlDefinitionParser.ParseQuery(sql);

            ValidateAllTableAccess(definition);

            var validationErrors = DefinitionValidator.Validate(definition);
            if (validationErrors.Count > 0)
                return "Validation failed:\n" + string.Join("\n", validationErrors);

            string result;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                result = await strategy.ExecuteQueryAsync(
                    definition,
                    sqlConfig.ConnectionString,
                    ResolveExecutionPolicy());
            }

            await _auditService.WriteEventAsync(
                "mcp.query.executed",
                definition.TableName ?? "unknown",
                "success",
                new AuditEventContext
                {
                    ToolName = "execute_query_sql",
                    Operation = "select",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ReturnedRows = CountJsonRows(result),
                    Definition = DescribeQuery(definition)
                },
                $"Provider: {dbType}");
            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteEventAsync(
                "mcp.query.executed",
                definition?.TableName ?? "unknown",
                "failed",
                new AuditEventContext
                {
                    ToolName = "execute_query_sql",
                    Operation = "select",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorCategory = ex.GetType().Name,
                    Definition = definition == null ? null : DescribeQuery(definition)
                },
                ex.Message);
            return "Execution failed: " + ex.Message;
        }
    }

    [McpServerTool, Description(@"
        Execute one DML SQL statement (INSERT, UPDATE, DELETE). The server parses SQL,
        validates it, applies table whitelist checks, then runs it inside a transaction
        (dry-run) and presents the result to you for approval via an interactive prompt.

        You will see how many rows would be affected before deciding to commit or cancel.
    ")]
    public async Task<string> ExecuteDmlSql(
        [Description("A single INSERT, UPDATE, or DELETE SQL statement to parse and validate.")]
        string sql,
        McpServer server,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        DmlDefinition? dml = null;
        int? affectedRowCount = null;
        try
        {
            ValidateToolAccess("execute_dml_sql");
            if (string.IsNullOrWhiteSpace(sql))
                return "Error: SQL is missing.";

            if (server.ClientCapabilities?.Elicitation == null)
                return "Error: This MCP client does not support the interactive confirmation required for DML execution.";

            dml = SqlDefinitionParser.ParseDml(sql);
            ValidateAllTableAccess(dml.TableName, null, null, null, dml.FromQuery, null, dml.WhereConditions);
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";

            var strategy = _sqlStrategyFactory.GetStrategy(dbType);

            var dmlErrors = DefinitionValidator.Validate(dml);
            if (dmlErrors.Count > 0)
                return "Validation failed:\n" + string.Join("\n", dmlErrors);

            // ── Dry-run ──────────────────────────────────────────────────────

            dml.ConfirmToken = null;
            var executionPolicy = ResolveExecutionPolicy();
            string dryRunResult;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                dryRunResult = await strategy.ExecuteDmlAsync(
                    sqlConfig.ConnectionString,
                    dml,
                    executionPolicy,
                    cancellationToken);
            }

            if (!dryRunResult.StartsWith("Dry Run Result", StringComparison.Ordinal))
            {
                await WriteDmlAuditAsync(dml, "failed", "not-requested", stopwatch, null, dryRunResult, cancellationToken);
                return dryRunResult;
            }

            var affectedMatch = AffectedMatchRegex().Match(dryRunResult);
            var tokenMatch = TokenMatchRegex().Match(dryRunResult);
            if (!affectedMatch.Success || !tokenMatch.Success)
                return dryRunResult;

            var affectedRows = affectedMatch.Groups[1].Value;
            affectedRowCount = int.TryParse(affectedRows, out var parsedRows) ? parsedRows : null;
            var detToken = tokenMatch.Groups[1].Value;

            // ── Present to user for approval via Elicitation ─────────────────

            var elicitSchema = new RequestSchema
            {
                Properties =
                {
                    ["approve"] = new BooleanSchema
                    {
                        Title = "Approve execution",
                        Description = $"This will {dml.Operation.ToString().ToLowerInvariant()} {affectedRows} row(s) in {dml.TableName}"
                    }
                }
            };

            var elicitResult = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = $"{dml.Operation} on {dml.TableName} — {affectedRows} row(s) affected\n\nSQL: {sql}",
                RequestedSchema = elicitSchema
            }, cancellationToken);

            if (elicitResult.Action != "accept" || elicitResult.Content?.TryGetValue("approve", out var approveEl) != true || approveEl.ValueKind != JsonValueKind.True)
            {
                await WriteDmlAuditAsync(
                    dml,
                    "cancelled",
                    "declined",
                    stopwatch,
                    affectedRowCount,
                    $"Operation: {dml.Operation} (cancelled through MCP interaction)",
                    cancellationToken);
                return "DML execution cancelled by user.";
            }

            // ── Execute for real ──────────────────────────────────────────────

            dml.ConfirmToken = detToken;
            string finalResult;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                finalResult = await strategy.ExecuteDmlAsync(
                    sqlConfig.ConnectionString,
                    dml,
                    executionPolicy,
                    cancellationToken);
            }

            if (!finalResult.StartsWith("Success", StringComparison.Ordinal))
            {
                await WriteDmlAuditAsync(
                    dml,
                    "failed",
                    "interactive-accepted",
                    stopwatch,
                    affectedRowCount,
                    finalResult,
                    cancellationToken);
                return finalResult;
            }

            await WriteDmlAuditAsync(
                dml,
                "success",
                "interactive-accepted",
                stopwatch,
                affectedRowCount,
                $"Operation: {dml.Operation} (committed after MCP interactive approval)",
                cancellationToken);
            return finalResult;
        }
        catch (Exception ex)
        {
            await _auditService.WriteEventAsync(
                "mcp.dml.executed",
                dml?.TableName ?? "unknown",
                "failed",
                new AuditEventContext
                {
                    ToolName = "execute_dml_sql",
                    Operation = dml?.Operation.ToString().ToLowerInvariant(),
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    AffectedRows = affectedRowCount,
                    ApprovalStatus = "not-completed",
                    ErrorCategory = ex.GetType().Name,
                    Definition = dml == null ? null : DescribeDml(dml)
                },
                ex.Message,
                cancellationToken);
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
            List<ColumnInfo> columns;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                columns = await strategy.GetColumnsAsync(sqlConfig.ConnectionString, schemaName, tableName);
            }

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
            IEnumerable<string> schemas;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                schemas = await strategy.GetSchemasAsync(sqlConfig.ConnectionString);
            }

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
            IEnumerable<string> tables;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                tables = await strategy.GetTablesAsync(sqlConfig.ConnectionString, schemaName);
            }

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

    private static readonly Regex ExtractPattern = new(
        @"EXTRACT\s*\(\s*(\w+)\s+FROM\s+([^()]+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ExtractQuarterPattern = new(
        @"EXTRACT\s*\(\s*QUARTER\s+FROM\s+([^()]+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    internal static string NormalizeSql(string sql)
    {
        sql = ExtractQuarterPattern.Replace(sql, m =>
            $"CEIL(MONTH({m.Groups[1].Value.Trim()}) / 3.0)");
        sql = ExtractPattern.Replace(sql, m =>
            $"{m.Groups[1].Value.ToUpperInvariant()}({m.Groups[2].Value.Trim()})");
        return sql;
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

    private async Task WriteDmlAuditAsync(
        DmlDefinition dml,
        string result,
        string approvalStatus,
        Stopwatch stopwatch,
        int? affectedRows,
        string detail,
        CancellationToken cancellationToken)
    {
        await _auditService.WriteEventAsync(
            "mcp.dml.executed",
            dml.TableName,
            result,
            new AuditEventContext
            {
                ToolName = "execute_dml_sql",
                Operation = dml.Operation.ToString().ToLowerInvariant(),
                DurationMs = stopwatch.ElapsedMilliseconds,
                AffectedRows = affectedRows,
                ApprovalStatus = approvalStatus,
                ErrorCategory = result == "failed" ? "PolicyOrExecutionDenied" : null,
                Definition = DescribeDml(dml)
            },
            detail,
            cancellationToken);
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

    private void ValidateAllTableAccess(QueryDefinition queryDef)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFromQueryDefinition(queryDef, referenced, aliases);
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
                    CollectFromWindowDefinition(funcSel.Window, referenced, aliases);
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
            else if (h is ExpressionHavingCondition exprHaving)
            {
                CollectFromExpression(exprHaving.LeftExpression, referenced, aliases);
                CollectFromExpression(exprHaving.RightExpression, referenced, aliases);
            }
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
            CollectFromWindowDefinition(func.Window, referenced, aliases);
        }
        else if (condition is OperationSelectCondition op)
        {
            CollectFromExpression(op.Left, referenced, aliases);
            CollectFromExpression(op.Right, referenced, aliases);
        }
        else if (condition is CaseWhenSelectCondition cw)
            CollectFromCaseWhenClauses(cw.CaseWhen, referenced, aliases);
        else if (condition is SubQuerySelectCondition sq)
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

    [GeneratedRegex(@"TokenRequired=(\S+)")]
    private static partial Regex TokenMatchRegex();
    [GeneratedRegex(@"affectedRows=(\d+)")]
    private static partial Regex AffectedMatchRegex();
}
