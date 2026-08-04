using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IRoleService
{
    Task<IEnumerable<RoleVM>> GetRolesAsync();
    Task<RoleVM> UpsertRoleAsync(int? id, RolePayload request);
    Task<RoleDependencyVM> GetRoleDependenciesAsync(int id);
    Task RemoveRoleAsync(int? id, bool force = false);
    Task<IEnumerable<PermissionActionTemplateVM>> GetPermissionActionTemplatesAsync();
}
