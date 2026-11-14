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
    // The runtime may retain compiler diagnostics or provider exceptions in its
    // internal Message for Audit. Only fixed status text and bounded, typed DDL
    // codes are permitted at the MCP transport boundary.
    private static string SafeRuntimeMessage(DdlRuntimeStatus status, string internalMessage) =>
        status switch
        {
            DdlRuntimeStatus.Rejected => "DDL request was rejected; review the DDL policy and audit code.",
            DdlRuntimeStatus.Failed => "DDL execution failed; inspect audit and physical schema state.",
            DdlRuntimeStatus.OutcomeUnknown =>
                "DDL outcome is unconfirmed. Do not retry; reconcile physical schema and approval request.",
            _ => internalMessage
        };

    private static McpToolError? SafeRuntimeError(DdlRuntimeStatus status, string? code)
    {
        if (status is not (DdlRuntimeStatus.Failed or DdlRuntimeStatus.Rejected
            or DdlRuntimeStatus.OutcomeUnknown))
            return null;

        var safeCode = status == DdlRuntimeStatus.OutcomeUnknown
            ? "ddl.outcome_unknown"
            : code is { Length: > 4 and <= 100 }
                && code.StartsWith("ddl.", StringComparison.Ordinal)
                && code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')
                ? code : "ddl.denied";
        return new McpToolError(safeCode, SafeRuntimeMessage(status, string.Empty), "DdlExecution");
    }

    /// <summary>One PostgreSQL atomic batch preview (not authorization).</summary>
    public IReadOnlyList<string>? BatchStatements { get; init; }
    public string? BatchPlanFingerprint { get; init; }
    public bool BatchAtomicityProofRequired { get; init; }
    public string? BatchHumanReadableDiff { get; init; }

    /// <summary>Risk evidence for human-facing DDL previews, never an execution grant.</summary>
    public string? Risk { get; init; }
    public bool MayLoseData { get; init; }
    public string? HumanReadableDiff { get; init; }

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
            SafeRuntimeMessage(result.Status, result.Message),
            SafeRuntimeError(result.Status, result.ErrorCode))
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
            SafeRuntimeMessage(result.Status, result.Message),
            SafeRuntimeError(result.Status, result.ErrorCode))
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
            SafeRuntimeMessage(result.Status, result.Message),
            SafeRuntimeError(result.Status, result.ErrorCode))
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
            null, preview?.ExpectedColumns, preview?.RequestId, SafeRuntimeMessage(result.Status, result.Message),
            SafeRuntimeError(result.Status, result.ErrorCode))
        {
            MySqlBefore = preview?.Before
        };
    }

    /// <summary>Opaque Oracle PDB metadata, not raw DBID/GUID or principal.</summary>
    public OracleDdlPhysicalSnapshot? OracleBefore { get; init; }

    public static McpDdlToolResult FromOracleRuntime(OracleDdlRuntimeResult result)
    {
        var preview = result.Preview;
        return new McpDdlToolResult(
            result.Status == DdlRuntimeStatus.OutcomeUnknown
                ? "outcome_unknown" : result.Status.ToString().ToLowerInvariant(),
            result.CommitConfirmed, "Oracle",
            preview?.StaticPreview.PlanFingerprint,
            preview?.StaticPreview.Plan.Command.Sql,
            null, preview?.ExpectedColumns, preview?.RequestId, SafeRuntimeMessage(result.Status, result.Message),
            SafeRuntimeError(result.Status, result.ErrorCode))
        {
            OracleBefore = preview?.Before
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
            SafeRuntimeMessage(result.Status, result.Message),
            SafeRuntimeError(result.Status, result.ErrorCode))
        {
            Risk = preview?.StaticPreview.Plan.Risk.ToString(),
            MayLoseData = preview?.StaticPreview.Plan.Impact.MayLoseData ?? false,
            HumanReadableDiff = preview?.HumanReadableDiff,
            BatchStatements = result.BatchPreview?.Plan.Plans.Select(p => p.Command.Sql).ToArray(),
            BatchPlanFingerprint = result.BatchPreview?.Plan.PlanFingerprint,
            BatchAtomicityProofRequired = result.BatchPreview is not null,
            BatchHumanReadableDiff = result.BatchPreview?.HumanReadableDiff
        };
    }
}
