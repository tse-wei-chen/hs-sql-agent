namespace Admin.Service.Models;

public class AuditEventContext
{
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
}
