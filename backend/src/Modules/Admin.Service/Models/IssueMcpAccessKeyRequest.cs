namespace Admin.Service.Models;

public class IssueMcpAccessKeyRequest
{
    public string Name { get; set; } = null!;
    public DateTime? ExpiresAt { get; set; }
    public string? AllowedTools { get; set; }
    public string? CorsAllowedOrigins { get; set; }
    public int DbSettingMode { get; set; }
    public int? DbManagementId { get; set; }
    public string? SqlProvider { get; set; }
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }
}
