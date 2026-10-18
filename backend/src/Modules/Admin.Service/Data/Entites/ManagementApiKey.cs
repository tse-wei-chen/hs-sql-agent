namespace Admin.Service.Data.Entites;

/// <summary>Human-issued credential for administration HTTP APIs; never valid on the MCP endpoint.</summary>
public sealed class ManagementApiKey
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string PermissionsJson { get; set; } = "[]";
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}
