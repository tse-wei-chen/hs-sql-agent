namespace Admin.Service.Data.Entites;

public class AuditLog
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string ActorType { get; set; } = "system";
    public string? ActorId { get; set; }
    public string Action { get; set; } = null!;
    public string Target { get; set; } = null!;
    public string? Detail { get; set; }
    public string Result { get; set; } = "success";
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? RequestId { get; set; }
    public string? SessionId { get; set; }
    public int? AccessKeyId { get; set; }
    public int? DbManagementId { get; set; }
    public string? DatabaseName { get; set; }
    public string? ToolName { get; set; }
    public string? Operation { get; set; }
    public long? DurationMs { get; set; }
    public int? ReturnedRows { get; set; }
    public int? AffectedRows { get; set; }
    public string? ApprovalStatus { get; set; }
    public string? ErrorCategory { get; set; }
    public string? Definition { get; set; }
    public DateTime CreatedAt { get; set; }
}
