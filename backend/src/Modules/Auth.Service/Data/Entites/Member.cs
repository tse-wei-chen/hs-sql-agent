namespace Auth.Service.Data.Entites;

public class Member
{
    public int Id { get; set; }
    public string Username { get; set; } = null!;
    public string Mail { get; set; } = null!;
    public string NormalizedMail { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public int SecurityVersion { get; set; } = 1;
    public int FailedSignInCount { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public bool RequirePasswordChangeAtNextSignIn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool MfaEnabled { get; set; }
    public string? MfaSecretProtected { get; set; }
    public ICollection<MemberRole> MemberRoles { get; set; } = [];
    public ICollection<AuthSession> AuthSessions { get; set; } = [];
    public ICollection<ExternalIdentity> ExternalIdentities { get; set; } = [];
    public ICollection<MfaRecoveryCode> MfaRecoveryCodes { get; set; } = [];
}
