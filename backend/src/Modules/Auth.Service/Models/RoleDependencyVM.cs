namespace Auth.Service.Models;

public class RoleDependencyVM
{
    public int RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public IReadOnlyCollection<MemberDependencyVM> Members { get; set; } = [];
    public IReadOnlyCollection<string> Permissions { get; set; } = [];
}

public class MemberDependencyVM
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Mail { get; set; } = string.Empty;
}
