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
            preview?.PlanFingerprint,
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
