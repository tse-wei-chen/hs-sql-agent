namespace Modules.Models;

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
    public bool HasSqlConnectionStringOverride { get; set; }
    public int? PermitLimitOverride { get; set; }
    public int? WindowSecondsOverride { get; set; }
    public int? QueueLimitOverride { get; set; }
    public DateTime CreatedAt { get; set; }
}
