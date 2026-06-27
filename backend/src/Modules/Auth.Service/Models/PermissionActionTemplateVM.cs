using System.Linq.Expressions;
using Auth.Service.Data.Entites;

namespace Auth.Service.Models;

public class PermissionActionTemplateVM
{
    public int Id { get; set; }
    public ActionVM? Action { get; set; }
    public PermissionVM? Permission { get; set; }
    public static Expression<Func<PermissionActionTemplate, PermissionActionTemplateVM>> Projection =>
            x => new PermissionActionTemplateVM
            {
                Id = x.Id,
                Permission = new PermissionVM
                {
                    Id = x.Permission.Id,
                    Name = x.Permission.Name,
                    Path = x.Permission.Path
                },
                Action = new ActionVM
                {
                    Id = x.Action.Id,
                    Code = x.Action.Code,
                    Name = x.Action.Name
                }
            };
}