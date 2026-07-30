namespace Admin.Service.Models;

public class McpAccessKeyListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public bool IsActive { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? AllowedTools { get; set; }
    public string? CorsAllowedOrigins { get; set; }
    public string? SqlProvider { get; set; }
    public int? DbManagementId { get; set; }
    public string? DbManagementName { get; set; }
    public bool IsExpired { get; set; }
    public bool IsExpiringSoon { get; set; }
    public string? TableWhitelist { get; set; }
    public DateTime CreatedAt { get; set; }
    public McpKeyRateLimitMode RateLimitMode { get; set; }
    public int? PermitLimitOverride { get; set; }
    public int? WindowSecondsOverride { get; set; }
    public int? EffectivePermitLimit { get; set; }
    public int? EffectiveWindowSeconds { get; set; }
}
