namespace Auth.Service.Data.Entites;

public class PermissionAction
{
    public int Id { get; set; }
    public int RoleId { get; set; }
    public int PermissionId { get; set; }
    public int ActionId { get; set; }
    public Role Role { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
    public AuthAction Action { get; set; } = null!;
}
