using System.ComponentModel;
using System.Text.Json;
using Admin.Service.Models;
using HsSqlAgent.Server.Models;
using ModelContextProtocol.Server;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    [McpServerTool, Description("Get column names and types of a table.")]
    public async Task<string> GetColumns(
        [Description("The schema name")] string schemaName,
        [Description("The table name")] string tableName)
    {
        try
        {
            ValidateToolAccess("get_columns");
            EnsureTableAllowed(QualifiedTable(schemaName, tableName));
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
            if (string.IsNullOrEmpty(tableName))
                return "Table name cannot be empty.";

            var provider = _sqlProviderFactory.GetProvider(dbType);
            List<ColumnInfo> columns;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                var metadata = await provider.Metadata.GetColumnsAsync(
                    sqlConfig.ConnectionString,
                    schemaName,
                    tableName);
                columns = metadata
                    .Select(column => new ColumnInfo(
                        column.Name,
                        column.Type,
                        column.IsPrimaryKey,
                        column.PrimaryKeyOrdinal))
                    .ToList();
            }

            var dbId = ResolveDbManagementId();
            if (dbId.HasValue)
            {
                var whitelist = ResolveTableWhitelist();
                var semanticModel = await _semanticService.GetSemanticModelAsync(dbId.Value);
                var tableSemantics = semanticModel.Entities.Where(s =>
                    string.Equals(s.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.TableName, tableName, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var col in columns)
                {
                    var semantic = tableSemantics.FirstOrDefault(s =>
                        string.Equals(s.ColumnName, col.Name, StringComparison.OrdinalIgnoreCase));
                    var parts = new List<string>();
                    if (semantic != null)
                    {
                        if (!string.IsNullOrWhiteSpace(semantic.DisplayName))
                            parts.Add($"Display Name: {semantic.DisplayName}");
                        if (!string.IsNullOrWhiteSpace(semantic.Description))
                            parts.Add(semantic.Description);
                        if (semantic.Synonyms.Count > 0)
                            parts.Add($"Synonyms: {string.Join(", ", semantic.Synonyms)}");
                    }

                    var relationships = semanticModel.Relationships.Where(r =>
                        IsSemanticTableAllowed(whitelist, r.SourceSchema, r.SourceTable)
                        && IsSemanticTableAllowed(whitelist, r.TargetSchema, r.TargetTable)
                        && ((SameIdentifier(r.SourceSchema, schemaName)
                             && SameIdentifier(r.SourceTable, tableName)
                             && SameIdentifier(r.SourceColumn, col.Name))
                            || (SameIdentifier(r.TargetSchema, schemaName)
                                && SameIdentifier(r.TargetTable, tableName)
                                && SameIdentifier(r.TargetColumn, col.Name))));
                    parts.AddRange(relationships.Select(DescribeRelationship));
                    if (parts.Count > 0)
                        col.Description = string.Join(". ", parts);
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
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";

            var provider = _sqlProviderFactory.GetProvider(dbType);
            IEnumerable<string> schemas;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                schemas = await provider.Metadata.GetSchemasAsync(sqlConfig.ConnectionString);
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
                return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";

            var provider = _sqlProviderFactory.GetProvider(dbType);
            IEnumerable<string> tables;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync())
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                tables = await provider.Metadata.GetTablesAsync(sqlConfig.ConnectionString, schemaName);
            }

            var whitelist = ResolveTableWhitelist();
            if (whitelist is { Count: > 0 })
                tables = tables.Where(t => whitelist.Contains(QualifiedTable(schemaName, t))).ToArray();

            var dbId = ResolveDbManagementId();
            if (dbId.HasValue)
            {
                var semanticModel = await _semanticService.GetSemanticModelAsync(dbId.Value);
                var tablesWithDesc = tables.Select(t =>
                {
                    var semantic = semanticModel.Entities.FirstOrDefault(item =>
                        SameIdentifier(item.SchemaName, schemaName)
                        && SameIdentifier(item.TableName, t)
                        && string.IsNullOrEmpty(item.ColumnName));
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(semantic?.DisplayName))
                        parts.Add($"Display Name: {semantic.DisplayName}");
                    if (!string.IsNullOrWhiteSpace(semantic?.Description))
                        parts.Add(semantic.Description);
                    if (semantic?.Synonyms.Count > 0)
                        parts.Add($"Synonyms: {string.Join(", ", semantic.Synonyms)}");
                    var metrics = semanticModel.Metrics.Where(metric =>
                        SameIdentifier(metric.SchemaName, schemaName) && SameIdentifier(metric.TableName, t));
                    parts.AddRange(metrics.Select(DescribeMetric));
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
                EnsureTableAllowed(QualifiedTable(entry.SchemaName, entry.TableName));
                var request = new DbSemanticRequest
                {
                    DbManagementId = dbId.Value,
                    SchemaName = entry.SchemaName,
                    TableName = entry.TableName,
                    ColumnName = entry.ColumnName,
                    Description = entry.Description,
                    DisplayName = entry.DisplayName,
                    Synonyms = entry.Synonyms
                };
                await _semanticService.UpsertSemanticAsync(request);
                var target = string.IsNullOrEmpty(entry.ColumnName)
                    ? QualifiedTable(entry.SchemaName ?? "dbo", entry.TableName)
                    : QualifiedColumn(entry.SchemaName ?? "dbo", entry.TableName, entry.ColumnName);
                results.Add($"  - {target}: updated");
            }

            await _auditService.WriteLogAsync(
                "mcp.update_semantic_layer",
                $"updated {entries.Count} entries",
                "success");
            return $"Semantic layer updated successfully:\n{string.Join("\n", results)}";
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                "mcp.update_semantic_layer",
                "semantic_layer",
                "failed",
                ex.Message);
            return $"Error updating semantic layer: {ex.Message}";
        }
    }

    private static bool SameIdentifier(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool IsSemanticTableAllowed(
        HashSet<string>? whitelist,
        string? schema,
        string table) =>
        whitelist is null or { Count: 0 } || whitelist.Contains(QualifiedTable(schema, table));

    private static string DescribeRelationship(DbSemanticRelationshipModel relationship) =>
        $"Relationship {relationship.Name}: "
        + $"{QualifiedColumn(relationship.SourceSchema, relationship.SourceTable, relationship.SourceColumn)} "
        + $"-> {QualifiedColumn(relationship.TargetSchema, relationship.TargetTable, relationship.TargetColumn)} "
        + $"[{relationship.Cardinality}, {relationship.Direction}]";

    private static string DescribeMetric(DbSemanticMetricModel metric)
    {
        var details = new List<string>
        {
            $"aggregation={metric.Aggregation}",
            $"formula={metric.Formula}"
        };
        if (!string.IsNullOrWhiteSpace(metric.Grain)) details.Add($"grain={metric.Grain}");
        if (!string.IsNullOrWhiteSpace(metric.Filter)) details.Add($"filter={metric.Filter}");
        if (metric.Synonyms is { Count: > 0 }) details.Add($"synonyms={string.Join("/", metric.Synonyms)}");
        return $"Metric {metric.DisplayName ?? metric.Name} [{string.Join("; ", details)}]";
    }

    private static string QualifiedColumn(string? schema, string table, string column) =>
        string.IsNullOrWhiteSpace(schema) ? $"{table}.{column}" : $"{schema}.{table}.{column}";

    private static string QualifiedTable(string? schema, string table) =>
        string.IsNullOrWhiteSpace(schema) ? table : $"{schema}.{table}";
}
