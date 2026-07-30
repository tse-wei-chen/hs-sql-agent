namespace Admin.Service.Models;

public class AuditLogFilter
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Action { get; set; }
    public string? Keyword { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Result { get; set; }
    public string? Actor { get; set; }
    public int? DbManagementId { get; set; }
    public int? AccessKeyId { get; set; }
    public string? ToolName { get; set; }
}
