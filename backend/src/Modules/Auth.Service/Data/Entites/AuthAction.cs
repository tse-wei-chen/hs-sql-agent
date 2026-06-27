namespace Auth.Service.Data.Entites;

public class AuthAction
{
    public int Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public ICollection<PermissionAction> PermissionActions { get; set; } = [];
    public ICollection<PermissionActionTemplate> PermissionActionTemplates { get; set; } = [];
}
