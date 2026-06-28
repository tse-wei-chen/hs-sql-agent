namespace Auth.Service.Data.Entites;

public class Permission
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Path { get; set; } = null!;
    public ICollection<PermissionAction> PermissionActions { get; set; } = [];
    public ICollection<PermissionActionTemplate> PermissionActionTemplates { get; set; } = [];
}
