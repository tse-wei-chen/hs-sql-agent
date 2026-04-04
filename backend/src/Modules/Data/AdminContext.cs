using Microsoft.EntityFrameworkCore;
using Modules.Data.Entites;

namespace Modules.Data;

public class AdminContext(DbContextOptions<AdminContext> options) : DbContext(options), IAdminContext
{
    public DbSet<SuperUser> SuperUsers { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdminContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return base.SaveChangesAsync(cancellationToken);
    }
}