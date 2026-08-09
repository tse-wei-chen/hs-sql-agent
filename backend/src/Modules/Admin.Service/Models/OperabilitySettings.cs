namespace Admin.Service.Models;

public class OperabilitySettings
{
    public bool HealthProbeEnabled { get; set; } = true;
    public int HealthProbeIntervalSeconds { get; set; } = 60;
    public int HealthProbeTimeoutSeconds { get; set; } = 10;
    public int SlowQueryThresholdMs { get; set; } = 1000;
    public string AlertWebhookUrl { get; set; } = string.Empty;
    public string AlertWebhookSecret { get; set; } = string.Empty;
    public string SiemWebhookUrl { get; set; } = string.Empty;
    public string SiemWebhookSecret { get; set; } = string.Empty;
    public int DeliveryMaxAttempts { get; set; } = 6;
    public int AuditRetentionDays { get; set; }
    public string AuditRetentionMode { get; set; } = "Purge";
    public string AuditArchivePath { get; set; } = "data/audit-archive";
    public string AuditFallbackPath { get; set; } = "data/audit-fallback.jsonl";
    public int AuditRetentionRunHourUtc { get; set; } = 2;
}
