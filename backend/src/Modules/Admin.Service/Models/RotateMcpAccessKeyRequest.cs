namespace Admin.Service.Models;

public class RotateMcpAccessKeyRequest
{
    public int GracePeriodMinutes { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
