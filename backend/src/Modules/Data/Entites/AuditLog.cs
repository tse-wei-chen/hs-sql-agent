namespace Modules.Data.Entites;

public class AuditLog
{
    public long Id { get; set; }
    public string ActorType { get; set; } = "system";
    public string? ActorId { get; set; }
    public string Action { get; set; } = null!;
    public string Target { get; set; } = null!;
    public string? Detail { get; set; }
    public string Result { get; set; } = "success";
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}
