namespace Admin.Service.Models;

public class OperabilityFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int? DbManagementId { get; set; }
    public int? AccessKeyId { get; set; }
    public string? ToolName { get; set; }
}

public class ExecutionMetricSummary
{
    public long QueryCount { get; set; }
    public long DmlCount { get; set; }
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
    public double SuccessRate { get; set; }
    public double? P50LatencyMs { get; set; }
    public double? P95LatencyMs { get; set; }
    public int LatencySampleSize { get; set; }
    public bool LatencySampled { get; set; }
    public long SlowQueryCount { get; set; }
    public long IpRateLimitCount { get; set; }
    public long KeyRateLimitCount { get; set; }
}

public class DbHealthItem
{
    public int DbManagementId { get; set; }
    public string Name { get; set; } = null!;
    public string? Provider { get; set; }
    public string Status { get; set; } = "unknown";
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public long? LatencyMs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? OutageStartedAt { get; set; }
    public string? LastError { get; set; }
}

public class KeyUsageItem
{
    public int AccessKeyId { get; set; }
    public string Name { get; set; } = null!;
    public DateTime? LastUsedAt { get; set; }
    public long RequestCount { get; set; }
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
    public long RateLimitCount { get; set; }
    public double RateLimitRejectionRate { get; set; }
}

public class DeliveryStatusItem
{
    public long Id { get; set; }
    public string Category { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}

public class AuditRetentionResult
{
    public DateTime Cutoff { get; set; }
    public long MatchingCount { get; set; }
    public bool DryRun { get; set; }
    public string Mode { get; set; } = null!;
    public string? ArchiveFile { get; set; }
    public long DeletedCount { get; set; }
}
