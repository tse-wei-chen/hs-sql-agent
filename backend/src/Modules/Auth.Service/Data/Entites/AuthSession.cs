namespace Auth.Service.Data.Entites;

public class AuthSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TokenFamilyId { get; set; } = Guid.NewGuid();
    public int MemberId { get; set; }
    public string CurrentRefreshTokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public Member Member { get; set; } = null!;
}
