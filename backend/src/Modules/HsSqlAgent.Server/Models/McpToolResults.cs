using System.ComponentModel;

namespace HsSqlAgent.Server.Models;

/// <summary>
/// Machine-readable error details returned by structured MCP tool results.
/// </summary>
public sealed record McpToolError(
    [property: Description("Stable machine-readable error code.")]
    string Code,
    [property: Description("Human-readable error message safe to surface to the MCP client.")]
    string Message,
    [property: Description("Pipeline or execution stage that rejected the request, when known.")]
    string? Stage = null,
    [property: Description("Whether retrying the same request later may succeed without changing the input.")]
    bool Retryable = false);

/// <summary>
/// Structured output for the built-in execute_query_sql MCP tool.
/// </summary>
public sealed record McpQueryToolResult(
    [property: Description("Whether the query completed successfully.")]
    bool Success,
    [property: Description("Resolved target database provider, or null when configuration could not be resolved.")]
    string? Provider,
    [property: Description("Number of rows returned by the query.")]
    int RowCount,
    [property: Description("Database execution duration in milliseconds, excluding MCP response serialization.")]
    long DurationMs,
    [property: Description("Returned rows. Each row is a JSON object keyed by column name.")]
    IReadOnlyList<Dictionary<string, object?>> Rows,
    [property: Description("Machine-readable error details when Success is false; otherwise null.")]
    McpToolError? Error)
{
    public static McpQueryToolResult Succeeded(
        string provider,
        int rowCount,
        TimeSpan duration,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        new(
            true,
            provider,
            rowCount,
            Math.Max(0, (long)duration.TotalMilliseconds),
            rows.Select(row => row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)).ToArray(),
            null);

    public static McpQueryToolResult Failed(
        string? provider,
        McpToolError error,
        long durationMs = 0) =>
        new(false, provider, 0, Math.Max(0, durationMs), [], error);
}

public sealed record McpSchemasToolResult(
    [property: Description("Whether schema discovery completed successfully.")]
    bool Success,
    [property: Description("Resolved target database provider, or null when configuration could not be resolved.")]
    string? Provider,
    [property: Description("Schema names visible to the current MCP key.")]
    IReadOnlyList<string> Schemas,
    [property: Description("Machine-readable error details when Success is false; otherwise null.")]
    McpToolError? Error);

public sealed record McpMetricToolItem(
    string Name,
    string? DisplayName,
    string Aggregation,
    string Formula,
    string? Grain,
    string? Filter,
    IReadOnlyList<string> Synonyms);

public sealed record McpTableToolItem(
    string Name,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Synonyms,
    IReadOnlyList<McpMetricToolItem> Metrics);

public sealed record McpTablesToolResult(
    [property: Description("Whether table discovery completed successfully.")]
    bool Success,
    string? Provider,
    string Schema,
    [property: Description("Tables visible to the current MCP key, including structured semantic metadata when available.")]
    IReadOnlyList<McpTableToolItem> Tables,
    McpToolError? Error);

public sealed record McpRelationshipToolItem(
    string Name,
    string Source,
    string Target,
    string Cardinality,
    string Direction);

public sealed record McpColumnToolItem(
    string Name,
    string Type,
    bool IsPrimaryKey,
    int? PrimaryKeyOrdinal,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Synonyms,
    IReadOnlyList<McpRelationshipToolItem> Relationships);

public sealed record McpColumnsToolResult(
    [property: Description("Whether column discovery completed successfully.")]
    bool Success,
    string? Provider,
    string Schema,
    string Table,
    [property: Description("Columns visible to the current MCP key, including structured semantic metadata and relationships when available.")]
    IReadOnlyList<McpColumnToolItem> Columns,
    McpToolError? Error);
