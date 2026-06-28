namespace Auth.Service.Data.Entites;

public class PermissionActionTemplate
{
    public int Id { get; set; }
    public int PermissionId { get; set; }
    public int ActionId { get; set; }
    public Permission Permission { get; set; } = null!;
    public AuthAction Action { get; set; } = null!;
}
