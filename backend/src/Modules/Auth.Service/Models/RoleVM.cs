using System.Linq.Expressions;
using Auth.Service.Data.Entites;

namespace Auth.Service.Models;
public class RoleVM
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public PermissionActionVM[] PermissionActions { get; set; } = [];
    public static Expression<Func<Role, RoleVM>> Projection =>
        x => new RoleVM
        {
            Id = x.Id,
            Name = x.Name,
            Description = x.Description,
            PermissionActions = x.PermissionActions
                .OrderBy(pa => pa.Id)
                .Select(pa => new PermissionActionVM
                {
                    PermissionId = pa.PermissionId,
                    ActionId = pa.ActionId
                })
                .ToArray()
        };
}