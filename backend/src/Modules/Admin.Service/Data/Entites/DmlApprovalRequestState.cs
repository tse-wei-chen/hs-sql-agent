namespace Admin.Service.Data.Entites;

/// <summary>
/// Durable server-owned state for a DML approval that an external provider has left pending.
/// SQL and resume metadata are stored only in ProtectedExecutionPayload.
/// </summary>
public sealed class DmlApprovalRequestState
{
    public string RequestId { get; set; } = string.Empty;
    public string ApprovalFingerprint { get; set; } = string.Empty;
    public string EvidenceFingerprint { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string ProtectedExecutionPayload { get; set; } = string.Empty;
    public string RequesterIdentity { get; set; } = string.Empty;
    public string TargetIdentity { get; set; } = string.Empty;
    public string DatabaseProvider { get; set; } = string.Empty;
    public string DatabaseIdentity { get; set; } = string.Empty;
    public int AccessKeyId { get; set; }
    public int DbManagementId { get; set; }
    public string RequiredToolName { get; set; } = string.Empty;
    public int? CustomToolId { get; set; }
    public int? CustomToolRevisionId { get; set; }
    public int StatementCount { get; set; }
    public int TotalAffectedRows { get; set; }
    public string? ExternalReference { get; set; }
    public string? ApproverIdentity { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
