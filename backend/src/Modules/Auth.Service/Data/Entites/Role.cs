namespace Auth.Service.Data.Entites;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public ICollection<MemberRole> MemberRoles { get; set; } = [];
    public ICollection<PermissionAction> PermissionActions { get; set; } = [];
}
