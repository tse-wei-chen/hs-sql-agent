using Microsoft.EntityFrameworkCore;
using Auth.Service.Data.Entites;

namespace Auth.Service.Data;

public interface IAuthContext
{
    DbSet<AuthAction> AuthActions { get; }
    DbSet<AuthSession> AuthSessions { get; }
    DbSet<Member> Members { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<MemberRole> MemberRoles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<PermissionAction> PermissionActions { get; }
    DbSet<PermissionActionTemplate> PermissionActionTemplates { get; }
    DbSet<Role> Roles { get; }
    DbSet<TokenBlacklistEntry> TokenBlacklistEntries { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
