namespace Admin.Service.Data.Entites;

public class OutboundDelivery
{
    public long Id { get; set; }
    public string Category { get; set; } = null!;
    public string DedupeKey { get; set; } = null!;
    public string TargetUrl { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}
