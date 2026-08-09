namespace Auth.Service.Data.Entites;

public class Member
{
    public int Id { get; set; }
    public string Username { get; set; } = null!;
    public string Mail { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public int SecurityVersion { get; set; } = 1;
    public ICollection<MemberRole> MemberRoles { get; set; } = [];
}
