namespace Auth.Service.Data.Entites;

public class MfaRecoveryCode
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UsedAt { get; set; }
    public Member Member { get; set; } = null!;
}
