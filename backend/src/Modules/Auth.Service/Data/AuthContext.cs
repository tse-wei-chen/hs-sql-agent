using Microsoft.EntityFrameworkCore;
using Auth.Service.Data.Entites;

namespace Auth.Service.Data;

public class AuthContext(DbContextOptions<AuthContext> options) : DbContext(options), IAuthContext
{
    public DbSet<AuthAction> AuthActions { get; set; } = null!;
    public DbSet<Member> Members { get; set; } = null!;
    public DbSet<MemberRole> MemberRoles { get; set; } = null!;
    public DbSet<Permission> Permissions { get; set; } = null!;
    public DbSet<PermissionAction> PermissionActions { get; set; } = null!;
    public DbSet<PermissionActionTemplate> PermissionActionTemplates { get; set; } = null!;
    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<TokenBlacklistEntry> TokenBlacklistEntries { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return base.SaveChangesAsync(cancellationToken);
    }
}
