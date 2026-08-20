namespace Admin.Service.Data.Entites;

using Admin.Service.Models;

public class McpAccessKey
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public string KeyHash { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? AllowedTools { get; set; }
    public string? CorsAllowedOrigins { get; set; }
    public string? SqlProvider { get; set; }
    public int? DbManagementId { get; set; }
    public string? TableWhitelist { get; set; }
    public McpKeyRateLimitMode RateLimitMode { get; set; } = McpKeyRateLimitMode.Inherit;
    public int? PermitLimitOverride { get; set; }
    public int? WindowSecondsOverride { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? BootstrapId { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}
