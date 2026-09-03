using System.Data;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Auth.Service.Services;

public class RoleService(
    IAuthContext context,
    IAuthRuntimeStateCache? authRuntimeStateCache = null) : IRoleService
{
    private readonly IAuthContext _context = context;
    private readonly IAuthRuntimeStateCache? _authRuntimeStateCache = authRuntimeStateCache;

    private static bool IsSuperUser(string name) =>
        string.Equals(name, AuthService.SuperUserRoleName, StringComparison.OrdinalIgnoreCase);

    public async Task<IEnumerable<RoleVM>> GetRolesAsync()
    {
        return await _context.Roles
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(RoleVM.Projection)
            .ToListAsync();
    }

    public async Task<RoleVM> UpsertRoleAsync(int? id, RolePayload request)
    {
        if (IsSuperUser(request.Name))
            throw new InvalidOperationException("Cannot modify the built-in SuperUser role.");

        var role = (id is null
            ? new Role()
            : await _context.Roles
                .Include(x => x.PermissionActions)
                .FirstOrDefaultAsync(x => x.Id == id.Value)) ?? throw new InvalidOperationException("Role not found.");

        if (id is not null && IsSuperUser(role.Name))
            throw new InvalidOperationException("Cannot modify the built-in SuperUser role.");

        var name = request.Name.Trim();
        var normalizedPermissionActions = request.PermissionActions
            .GroupBy(x => new { x.PermissionId, x.ActionId })
            .Select(x => x.Key)
            .ToArray();

        if (await _context.Roles.AnyAsync(x => x.Id != role.Id && x.Name == name))
        {
            throw new InvalidOperationException("Role name already exists.");
        }

        var validTemplatePairs = await _context.PermissionActionTemplates
            .AsNoTracking()
            .Select(x => new { x.PermissionId, x.ActionId })
            .ToListAsync();

        var templateLookup = validTemplatePairs
            .Select(x => (x.PermissionId, x.ActionId))
            .ToHashSet();

        role.Name = name;
        role.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (id is null)
        {
            _context.Roles.Add(role);
            await _context.SaveChangesAsync();
        }

        var existingPermissionActions = await _context.PermissionActions
            .Where(x => x.RoleId == role.Id)
            .ToListAsync();
        _context.PermissionActions.RemoveRange(existingPermissionActions);

        var affectedMembers = id is null
            ? []
            : await _context.Members
                .Where(x => x.MemberRoles.Any(mr => mr.RoleId == role.Id))
                .ToListAsync();

        foreach (var template in normalizedPermissionActions.Where(x => templateLookup.Contains((x.PermissionId, x.ActionId))))
        {
            _context.PermissionActions.Add(new PermissionAction
            {
                RoleId = role.Id,
                PermissionId = template.PermissionId,
                ActionId = template.ActionId
            });
        }

        foreach (var member in affectedMembers)
            member.SecurityVersion++;

        await RunSecurityMutationsAsync(
            affectedMembers.Select(member => member.Id).ToArray(),
            "Role permissions changed.",
            ct => _context.SaveChangesAsync(ct),
            CancellationToken.None);

        return await _context.Roles
            .AsNoTracking()
            .Where(x => x.Id == role.Id)
            .Select(RoleVM.Projection)
            .FirstAsync();
    }

    public async Task<RoleDependencyVM> GetRoleDependenciesAsync(int id)
    {
        var role = await _context.Roles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidOperationException("Role Not Found");
        return new RoleDependencyVM
        {
            RoleId = role.Id,
            RoleName = role.Name,
            Permissions = await _context.PermissionActions.AsNoTracking()
                .Where(x => x.RoleId == id)
                .OrderBy(x => x.Permission.Path).ThenBy(x => x.Action.Code)
                .Select(x => x.Permission.Path + "." + x.Action.Code)
                .ToListAsync(),
            Members = await _context.Members.AsNoTracking()
                .Where(x => x.MemberRoles.Any(mr => mr.RoleId == id))
                .OrderBy(x => x.Mail)
                .Select(x => new MemberDependencyVM { Id = x.Id, Username = x.Username, Mail = x.Mail })
                .ToListAsync()
        };
    }

    public async Task RemoveRoleAsync(int? id, bool force = false)
    {
        await using var transaction = _context is AuthContext db
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        var role = await _context.Roles.FirstOrDefaultAsync(x => x.Id == id) ?? throw new InvalidOperationException("Role Not Found");

        if (IsSuperUser(role.Name))
            throw new InvalidOperationException("Cannot delete the built-in SuperUser role.");

        var affectedMembers = await _context.Members
            .Where(x => x.MemberRoles.Any(mr => mr.RoleId == role.Id))
            .ToListAsync();
        if (affectedMembers.Count > 0 && !force)
            throw new InvalidOperationException($"Role is assigned to {affectedMembers.Count} member(s). Review dependencies and confirm removal.");
        foreach (var member in affectedMembers)
            member.SecurityVersion++;

        _context.Roles.Remove(role);
        await RunSecurityMutationsAsync(
            affectedMembers.Select(member => member.Id).ToArray(),
            "Assigned role removed.",
            async ct =>
            {
                await _context.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
            },
            CancellationToken.None);
    }

    private Task RunSecurityMutationsAsync(
        IReadOnlyCollection<int> memberIds,
        string reason,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken) =>
        _authRuntimeStateCache is null
            ? mutation(cancellationToken)
            : _authRuntimeStateCache.RunWithBarriersAsync(
                memberIds,
                reason,
                mutation,
                cancellationToken);

    public async Task<IEnumerable<PermissionActionTemplateVM>> GetPermissionActionTemplatesAsync()
    {
        return await _context.PermissionActionTemplates
            .AsNoTracking()
            .OrderBy(x => x.Permission.Path)
            .ThenBy(x => x.Action.Code)
            .Select(PermissionActionTemplateVM.Projection)
            .ToListAsync();
    }
}
