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

/// <summary>
/// Structured output for the built-in execute_dml_sql MCP tool.
/// </summary>
public sealed record McpDmlToolResult(
    [property: Description("Machine-readable DML state: committed, pending, rejected, or failed.")]
    string Status,
    [property: Description("Resolved target database provider, or null when configuration could not be resolved.")]
    string? Provider,
    [property: Description("Whether the transaction committed database changes.")]
    bool Committed,
    [property: Description("Number of DML statements in the parsed atomic batch. Zero when parsing did not complete.")]
    int StatementCount,
    [property: Description("Affected-row count from the approval preview for pending/rejected states, or from the commit result for committed/failed commit states.")]
    int? AffectedRows,
    [property: Description("Time spent waiting for the approval provider, in milliseconds.")]
    long ApprovalWaitDurationMs,
    [property: Description("Approval-provider decision when a request reached approval: approved, pending, or rejected.")]
    string? ApprovalDecision,
    [property: Description("Stable approval request identifier when an approval request was created.")]
    string? ApprovalRequestId,
    [property: Description("External approval-system reference when supplied by the configured approval provider.")]
    string? ApprovalExternalReference,
    [property: Description("Rows produced by an approved DML result clause such as RETURNING. Empty unless the transaction committed.")]
    IReadOnlyList<Dictionary<string, object?>> ReturnedRows,
    [property: Description("Human-readable outcome detail retained for text-content compatibility.")]
    string Message,
    [property: Description("Machine-readable error details only when Status is failed; otherwise null.")]
    McpToolError? Error)
{
    public static McpDmlToolResult CommittedResult(
        string provider,
        int statementCount,
        int? affectedRows,
        long approvalWaitDurationMs,
        string? approvalRequestId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? returnedRows,
        string message) =>
        new(
            "committed",
            provider,
            true,
            statementCount,
            affectedRows,
            Math.Max(0, approvalWaitDurationMs),
            "approved",
            approvalRequestId,
            null,
            MaterializeRows(returnedRows),
            message,
            null);

    public static McpDmlToolResult Pending(
        string provider,
        int statementCount,
        int? affectedRows,
        long approvalWaitDurationMs,
        string? approvalRequestId,
        string? approvalExternalReference,
        string message) =>
        new(
            "pending",
            provider,
            false,
            statementCount,
            affectedRows,
            Math.Max(0, approvalWaitDurationMs),
            "pending",
            approvalRequestId,
            approvalExternalReference,
            [],
            message,
            null);

    public static McpDmlToolResult Rejected(
        string provider,
        int statementCount,
        int? affectedRows,
        long approvalWaitDurationMs,
        string? approvalRequestId,
        string message) =>
        new(
            "rejected",
            provider,
            false,
            statementCount,
            affectedRows,
            Math.Max(0, approvalWaitDurationMs),
            "rejected",
            approvalRequestId,
            null,
            [],
            message,
            null);

    public static McpDmlToolResult Failed(
        string? provider,
        int statementCount,
        string message,
        McpToolError error,
        int? affectedRows = null,
        long approvalWaitDurationMs = 0,
        string? approvalDecision = null,
        string? approvalRequestId = null,
        string? approvalExternalReference = null) =>
        new(
            "failed",
            provider,
            false,
            Math.Max(0, statementCount),
            affectedRows,
            Math.Max(0, approvalWaitDurationMs),
            approvalDecision,
            approvalRequestId,
            approvalExternalReference,
            [],
            message,
            error);

    private static IReadOnlyList<Dictionary<string, object?>> MaterializeRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows) =>
        rows is null
            ? []
            : rows.Select(row => row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)).ToArray();
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
