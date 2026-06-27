using Microsoft.EntityFrameworkCore;
using Auth.Service.Data.Entites;

namespace Auth.Service.Data;

public interface IAuthContext
{
    DbSet<AuthAction> AuthActions { get; }
    DbSet<Member> Members { get; }
    DbSet<MemberRole> MemberRoles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<PermissionAction> PermissionActions { get; }
    DbSet<PermissionActionTemplate> PermissionActionTemplates { get; }
    DbSet<Role> Roles { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
