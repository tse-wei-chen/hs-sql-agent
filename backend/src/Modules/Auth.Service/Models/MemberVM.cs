namespace Auth.Service.Models;

public class MemberVM
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Mail { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool RequirePasswordChangeAtNextSignIn { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int ActiveSessionCount { get; set; }
    public int[] RoleIds { get; set; } = [];
    public string[] Roles { get; set; } = [];
}
