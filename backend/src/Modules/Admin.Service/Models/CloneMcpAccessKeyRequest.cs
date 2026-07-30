namespace Admin.Service.Models;

public class CloneMcpAccessKeyRequest
{
    public string Name { get; set; } = null!;
    public DateTime? ExpiresAt { get; set; }
}
