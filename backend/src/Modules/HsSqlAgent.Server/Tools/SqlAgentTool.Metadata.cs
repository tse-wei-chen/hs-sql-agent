using System.ComponentModel;
using Admin.Service.Models;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Models;
using ModelContextProtocol.Server;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("Get column names and types of a table.")]
    public async Task<McpColumnsToolResult> GetColumns(
        [Description("The schema name")] string schemaName,
        [Description("The table name")] string tableName,
        CancellationToken cancellationToken = default)
    {
        string? providerName = null;
        try
        {
            ValidateToolAccess("get_columns");
            EnsureTableAllowed(QualifiedTable(schemaName, tableName));
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
            {
                return new McpColumnsToolResult(
                    false,
                    null,
                    schemaName,
                    tableName,
                    [],
                    InvalidConfigurationError());
            }
            providerName = dbType.ToString();
            if (string.IsNullOrEmpty(tableName))
            {
                return new McpColumnsToolResult(
                    false,
                    providerName,
                    schemaName,
                    tableName,
                    [],
                    new McpToolError("validation.table_missing", "Table name cannot be empty.", "Input"));
            }

            var provider = _sqlProviderFactory.GetProvider(dbType);
            IReadOnlyList<DatabaseColumnMetadata> metadata;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                metadata = await provider.Metadata.GetColumnsAsync(
                    sqlConfig.ConnectionString,
                    schemaName,
                    tableName,
                    cancellationToken);
            }

            var whitelist = ResolveTableWhitelist();
            var dbId = ResolveDbManagementId();
            DbSemanticModel? semanticModel = null;
            if (dbId.HasValue)
                semanticModel = await _semanticService.GetSemanticModelAsync(dbId.Value, cancellationToken);

            var columns = metadata.Select(column =>
            {
                var semantic = semanticModel?.Entities.FirstOrDefault(s =>
                    string.Equals(s.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.TableName, tableName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.ColumnName, column.Name, StringComparison.OrdinalIgnoreCase));

                var relationships = semanticModel?.Relationships.Where(r =>
                    IsSemanticTableAllowed(whitelist, r.SourceSchema, r.SourceTable)
                    && IsSemanticTableAllowed(whitelist, r.TargetSchema, r.TargetTable)
                    && ((SameIdentifier(r.SourceSchema, schemaName)
                         && SameIdentifier(r.SourceTable, tableName)
                         && SameIdentifier(r.SourceColumn, column.Name))
                        || (SameIdentifier(r.TargetSchema, schemaName)
                            && SameIdentifier(r.TargetTable, tableName)
                            && SameIdentifier(r.TargetColumn, column.Name))))
                    .Select(r => new McpRelationshipToolItem(
                        r.Name,
                        QualifiedColumn(r.SourceSchema, r.SourceTable, r.SourceColumn),
                        QualifiedColumn(r.TargetSchema, r.TargetTable, r.TargetColumn),
                        r.Cardinality.ToString(),
                        r.Direction.ToString()))
                    .ToArray() ?? [];

                return new McpColumnToolItem(
                    column.Name,
                    column.Type,
                    column.IsPrimaryKey,
                    column.PrimaryKeyOrdinal,
                    semantic?.DisplayName,
                    semantic?.Description,
                    semantic?.Synonyms?.ToArray() ?? [],
                    relationships);
            }).ToArray();

            await _auditService.WriteLogAsync(
                "mcp.get_columns",
                $"{schemaName}.{tableName}",
                "success",
                cancellationToken: cancellationToken);
            return new McpColumnsToolResult(true, providerName, schemaName, tableName, columns, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                "mcp.get_columns",
                $"{schemaName}.{tableName}",
                "failed",
                ex.Message,
                cancellationToken);
            return new McpColumnsToolResult(
                false,
                providerName,
                schemaName,
                tableName,
                [],
                DescribeMetadataError(ex));
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("Get list of schemas in the database.")]
    public async Task<McpSchemasToolResult> GetSchemas(CancellationToken cancellationToken = default)
    {
        string? providerName = null;
        try
        {
            ValidateToolAccess("get_schemas");
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
                return new McpSchemasToolResult(false, null, [], InvalidConfigurationError());
            providerName = dbType.ToString();

            var provider = _sqlProviderFactory.GetProvider(dbType);
            IReadOnlyList<string> schemas;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                schemas = await provider.Metadata.GetSchemasAsync(sqlConfig.ConnectionString, cancellationToken);
            }

            await _auditService.WriteLogAsync(
                "mcp.get_schemas",
                "database",
                "success",
                cancellationToken: cancellationToken);
            return new McpSchemasToolResult(true, providerName, schemas.ToArray(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                "mcp.get_schemas",
                "database",
                "failed",
                ex.Message,
                cancellationToken);
            return new McpSchemasToolResult(false, providerName, [], DescribeMetadataError(ex));
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("Get list of tables in a schema.")]
    public async Task<McpTablesToolResult> GetTables(
        [Description("The schema name")] string schemaName,
        CancellationToken cancellationToken = default)
    {
        string? providerName = null;
        try
        {
            ValidateToolAccess("get_tables");
            var sqlConfig = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
                return new McpTablesToolResult(false, null, schemaName, [], InvalidConfigurationError());
            providerName = dbType.ToString();

            var provider = _sqlProviderFactory.GetProvider(dbType);
            IReadOnlyList<string> providerTables;
            await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
            {
                if (lease is null)
                    throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                providerTables = await provider.Metadata.GetTablesAsync(sqlConfig.ConnectionString, schemaName, cancellationToken);
            }

            IEnumerable<string> tables = providerTables;
            var whitelist = ResolveTableWhitelist();
            if (whitelist is { Count: > 0 })
                tables = tables.Where(t => whitelist.Contains(QualifiedTable(schemaName, t)));
            var visibleTables = tables.ToArray();

            DbSemanticModel? semanticModel = null;
            var dbId = ResolveDbManagementId();
            if (dbId.HasValue)
                semanticModel = await _semanticService.GetSemanticModelAsync(dbId.Value, cancellationToken);

            var tableItems = visibleTables.Select(tableName =>
            {
                var semantic = semanticModel?.Entities.FirstOrDefault(item =>
                    SameIdentifier(item.SchemaName, schemaName)
                    && SameIdentifier(item.TableName, tableName)
                    && string.IsNullOrEmpty(item.ColumnName));
                var metrics = semanticModel?.Metrics
                    .Where(metric => SameIdentifier(metric.SchemaName, schemaName) && SameIdentifier(metric.TableName, tableName))
                    .Select(metric => new McpMetricToolItem(
                        metric.Name,
                        metric.DisplayName,
                        metric.Aggregation.ToString(),
                        metric.Formula,
                        metric.Grain,
                        metric.Filter,
                        metric.Synonyms?.ToArray() ?? []))
                    .ToArray() ?? [];

                return new McpTableToolItem(
                    tableName,
                    semantic?.DisplayName,
                    semantic?.Description,
                    semantic?.Synonyms?.ToArray() ?? [],
                    metrics);
            }).ToArray();

            await _auditService.WriteLogAsync(
                "mcp.get_tables",
                schemaName,
                "success",
                cancellationToken: cancellationToken);
            return new McpTablesToolResult(true, providerName, schemaName, tableItems, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _auditService.WriteLogAsync(
                "mcp.get_tables",
                schemaName,
                "failed",
                ex.Message,
                cancellationToken);
            return new McpTablesToolResult(false, providerName, schemaName, [], DescribeMetadataError(ex));
        }
    }

    // Semantic metadata is an administrative control-plane write. Keep this CLR method for
    // source/binary compatibility, but do not expose it as an MCP tool. Administrators edit
    // semantic metadata through the Admin API/UI where canonical permissions are enforced.
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

    private static McpToolError InvalidConfigurationError() =>
        new("configuration.invalid", InvalidSqlConfigurationMessage, "Configuration");

    private static McpToolError DescribeMetadataError(Exception error) => error switch
    {
        UnauthorizedAccessException => new McpToolError(
            "authorization.denied",
            error.Message,
            "Authorization"),
        TimeoutException => new McpToolError(
            "metadata.timeout",
            error.Message,
            "Metadata",
            true),
        InvalidOperationException when error.Message.StartsWith("Server busy:", StringComparison.Ordinal) =>
            new McpToolError(
                "server.busy",
                error.Message,
                "Metadata",
                true),
        _ => new McpToolError(
            "metadata.failed",
            error.Message,
            "Metadata")
    };

    private static bool SameIdentifier(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool IsSemanticTableAllowed(
        HashSet<string>? whitelist,
        string? schema,
        string table) =>
        whitelist is null or { Count: 0 } || whitelist.Contains(QualifiedTable(schema, table));

    private static string QualifiedColumn(string? schema, string table, string column) =>
        string.IsNullOrWhiteSpace(schema) ? $"{table}.{column}" : $"{schema}.{table}.{column}";

    private static string QualifiedTable(string? schema, string table) =>
        string.IsNullOrWhiteSpace(schema) ? table : $"{schema}.{table}";
}
