namespace Admin.Service.Data.Entites;

public class RateLimitMetric
{
    public long Id { get; set; }
    public DateTime BucketStart { get; set; }
    public string Layer { get; set; } = null!;
    public int? AccessKeyId { get; set; }
    public int? DbManagementId { get; set; }
    public string? ToolName { get; set; }
    public long AttemptCount { get; set; }
    public long RejectedCount { get; set; }
}
