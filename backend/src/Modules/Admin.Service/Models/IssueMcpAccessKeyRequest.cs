namespace Admin.Service.Models;

public class IssueMcpAccessKeyRequest
{
	public string Name { get; set; } = null!;
	public DateTime? ExpiresAt { get; set; }
	public string? AllowedTools { get; set; }
	public string? CorsAllowedOrigins { get; set; }
	public string? SqlProvider { get; set; }
	public string? SqlConnectionString { get; set; }
}
