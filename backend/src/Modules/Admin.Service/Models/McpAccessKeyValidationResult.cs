namespace Admin.Service.Models;

public class McpAccessKeyValidationResult
{
    public bool IsValid { get; set; }
    public int? KeyId { get; set; }
    public string? Name { get; set; }
    public string? AllowedTools { get; set; }
    public string? CorsAllowedOrigins { get; set; }
    public IReadOnlySet<string>? CorsAllowedOriginsSet { get; set; }
    public string? SqlProvider { get; set; }
    public int? DbManagementId { get; set; }
    public string? TableWhitelist { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public McpKeyRateLimitMode RateLimitMode { get; set; }
    public int? PermitLimitOverride { get; set; }
    public int? WindowSecondsOverride { get; set; }
    public string? Reason { get; set; }
}
