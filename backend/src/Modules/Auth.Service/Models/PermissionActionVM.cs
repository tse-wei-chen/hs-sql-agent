namespace Auth.Service.Models;

public class PermissionActionVM
{
    public int Id { get; set; }
    public int RoleId { get; set; }
    public int PermissionId { get; set; }
    public int ActionId { get; set; }
}