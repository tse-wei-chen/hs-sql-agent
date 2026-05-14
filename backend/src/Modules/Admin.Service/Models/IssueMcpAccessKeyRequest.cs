namespace Admin.Service.Models;

public class IssueMcpAccessKeyRequest
{
    public string Name { get; set; } = null!;
    public DateTime? ExpiresAt { get; set; }
    public string? AllowedTools { get; set; }
    public string? CorsAllowedOrigins { get; set; }
    public int DbManagementId { get; set; }
    public string? TableWhitelist { get; set; }
}
