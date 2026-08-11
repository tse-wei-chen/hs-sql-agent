namespace Auth.Service.Data.Entites;

public class ExternalIdentity
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Member Member { get; set; } = null!;
}
