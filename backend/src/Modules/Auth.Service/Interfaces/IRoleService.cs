using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IRoleService
{
    Task<IEnumerable<RoleVM>> GetRolesAsync();
    Task<RoleVM> UpsertRoleAsync(int? id, RolePayload request);
    Task RemoveRoleAsync(int? id);
    Task<IEnumerable<PermissionActionTemplateVM>> GetPermissionActionTemplatesAsync();
}