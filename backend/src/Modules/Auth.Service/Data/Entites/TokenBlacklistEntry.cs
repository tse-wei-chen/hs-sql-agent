namespace Auth.Service.Data.Entites;

public class TokenBlacklistEntry
{
    public int Id { get; set; }
    public string Jti { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime RevokedAt { get; set; }
}
