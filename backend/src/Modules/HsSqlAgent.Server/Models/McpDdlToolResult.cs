using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Models;

/// <summary>One, high-risk and independently approved built-in DDL tool response.</summary>
public sealed record McpDdlToolResult(
    string Status,
    bool CommitConfirmed,
    string? Provider,
    string? PlanFingerprint,
    string? NativeSql,
    DdlPhysicalSnapshot? Before,
    IReadOnlyList<DdlPhysicalColumn>? ExpectedColumns,
    string? ApprovalRequestId,
    string Message,
    McpToolError? Error)
{
    /// <summary>
    /// SQLite-specific physical snapshot. The PostgreSQL Before OID contract
    /// must not be reused for SQLite's schema cookie.
    /// </summary>
    public SqliteDdlPhysicalSnapshot? SqliteBefore { get; init; }

    public static McpDdlToolResult FromSqliteRuntime(SqliteDdlRuntimeResult result)
    {
        var preview = result.Preview;
        return new McpDdlToolResult(
            result.Status == DdlRuntimeStatus.OutcomeUnknown
                ? "outcome_unknown" : result.Status.ToString().ToLowerInvariant(),
            result.CommitConfirmed,
            "Sqlite",
            preview?.StaticPreview.PlanFingerprint,
            preview?.StaticPreview.Plan.Command.Sql,
            null,
            preview?.ExpectedColumns,
            preview?.RequestId,
            result.Message,
            result.Status is DdlRuntimeStatus.Failed or DdlRuntimeStatus.Rejected
                or DdlRuntimeStatus.OutcomeUnknown
                ? new McpToolError(result.ErrorCode ?? "ddl.denied", result.Message, "DdlExecution")
                : null)
        {
            // sqlite_schema.sql can contain private DEFAULT literals.
            // Host binds its full bytes into the approval fingerprint, but
            // MCP only receives structured physical metadata.
            SqliteBefore = preview?.Before is { } before
                ? before with
                {
                    TableDefinition = before.TableDefinition is null
                        ? null : "<schema-sql-redacted>"
                }
                : null
        };
    }

    /// <summary>Independent SQL Server DB_ID/schema_id/object_id contract.</summary>
    public SqlServerDdlPhysicalSnapshot? SqlServerBefore { get; init; }

    public static McpDdlToolResult FromSqlServerRuntime(SqlServerDdlRuntimeResult result)
    {
        var preview = result.Preview;
        return new McpDdlToolResult(
            result.Status == DdlRuntimeStatus.OutcomeUnknown
                ? "outcome_unknown" : result.Status.ToString().ToLowerInvariant(),
            result.CommitConfirmed,
            "MsSqlServer",
            preview?.StaticPreview.PlanFingerprint,
            preview?.StaticPreview.Plan.Command.Sql,
            null,
            preview?.ExpectedColumns,
            preview?.RequestId,
            result.Message,
            result.Status is DdlRuntimeStatus.Failed or DdlRuntimeStatus.Rejected
                or DdlRuntimeStatus.OutcomeUnknown
                ? new McpToolError(result.ErrorCode ?? "ddl.denied", result.Message, "DdlExecution")
                : null)
        {
            SqlServerBefore = preview?.Before
        };
    }

    /// <summary>
    /// Firebird physical identity contains only digests; never leak a database path
    /// or a live GUID through the MCP response.
    /// </summary>
    public FirebirdDdlPhysicalSnapshot? FirebirdBefore { get; init; }

    public static McpDdlToolResult FromFirebirdRuntime(FirebirdDdlRuntimeResult result)
    {
        var preview = result.Preview;
        return new McpDdlToolResult(
            result.Status == DdlRuntimeStatus.OutcomeUnknown
                ? "outcome_unknown" : result.Status.ToString().ToLowerInvariant(),
            result.CommitConfirmed,
            "Firebird",
            preview?.StaticPreview.PlanFingerprint,
            preview?.StaticPreview.Plan.Command.Sql,
            null,
            preview?.ExpectedColumns,
            preview?.RequestId,
            result.Message,
            result.Status is DdlRuntimeStatus.Failed or DdlRuntimeStatus.Rejected
                or DdlRuntimeStatus.OutcomeUnknown
                ? new McpToolError(result.ErrorCode ?? "ddl.denied", result.Message, "DdlExecution")
                : null)
        {
            FirebirdBefore = preview?.Before
        };
    }

    /// <summary>MySQL opaque physical server metadata; no raw UUID or credentials.</summary>
    public MySqlDdlPhysicalSnapshot? MySqlBefore { get; init; }

    public static McpDdlToolResult FromMySqlRuntime(MySqlDdlRuntimeResult result)
    {
        var preview = result.Preview;
        return new McpDdlToolResult(
            result.Status == DdlRuntimeStatus.OutcomeUnknown
                ? "outcome_unknown" : result.Status.ToString().ToLowerInvariant(),
            result.CommitConfirmed, "MySQL",
            preview?.StaticPreview.PlanFingerprint,
            preview?.StaticPreview.Plan.Command.Sql,
            null, preview?.ExpectedColumns, preview?.RequestId, result.Message,
            result.Status is DdlRuntimeStatus.Failed or DdlRuntimeStatus.Rejected
                or DdlRuntimeStatus.OutcomeUnknown
                ? new McpToolError(result.ErrorCode ?? "ddl.denied",
                    result.Message, "DdlExecution")
                : null)
        {
            MySqlBefore = preview?.Before
        };
    }

    public static McpDdlToolResult FromRuntime(DdlRuntimeResult result)
    {
        var preview = result.Preview;
        return new McpDdlToolResult(
            result.Status.ToString().ToLowerInvariant() switch
            {
                "outcomeunknown" => "outcome_unknown",
                var status => status
            },
            result.CommitConfirmed,
            "Postgres",
            preview?.StaticPreview.PlanFingerprint,
            preview?.StaticPreview.Plan.Command.Sql,
            preview?.Before,
            preview?.ExpectedColumns,
            preview?.RequestId,
            result.Message,
            result.Status is DdlRuntimeStatus.Failed or DdlRuntimeStatus.Rejected
                or DdlRuntimeStatus.OutcomeUnknown
                ? new McpToolError(result.ErrorCode ?? "ddl.denied", result.Message, "DdlExecution")
                : null);
    }
}
