namespace Admin.Service.Data.Entites;

public class DbHealthState
{
    public int Id { get; set; }
    public int DbManagementId { get; set; }
    public string Status { get; set; } = "unknown";
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public long? LatencyMs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? OutageStartedAt { get; set; }
    public string? LastError { get; set; }
}
