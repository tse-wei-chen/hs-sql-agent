namespace Auth.Service.Data.Entites;

public class ExternalLoginCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int MemberId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Member Member { get; set; } = null!;
}
