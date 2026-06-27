namespace Auth.Service.Models;

public class MemberVM
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Mail { get; set; } = string.Empty;
    public int[] RoleIds { get; set; } = [];
    public string[] Roles { get; set; } = [];
}
