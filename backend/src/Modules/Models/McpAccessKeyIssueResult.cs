namespace Modules.Models;

public class McpAccessKeyIssueResult
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public string PlaintextKey { get; set; } = null!;
    public DateTime? ExpiresAt { get; set; }
    public string? AllowedTools { get; set; }
    public string? CorsAllowedOrigins { get; set; }
    public string? SqlProvider { get; set; }
    public bool HasSqlConnectionStringOverride { get; set; }
}
