using Microsoft.EntityFrameworkCore;
using Admin.Service.Data.Entites;

namespace Admin.Service.Data;

public class AdminContext(DbContextOptions<AdminContext> options) : DbContext(options), IAdminContext
{
    public DbSet<AuditLog> AuditLogs { get; set; } = null!;
    public DbSet<McpAccessKey> McpAccessKeys { get; set; } = null!;
    public DbSet<CustomSqlTool> CustomSqlTools { get; set; } = null!;
    public DbSet<DbManagement> DbManagement { get; set; } = null!;
    public DbSet<DbSemantic> DbSemantics { get; set; } = null!;
    public DbSet<SecurityPolicySettings> SecurityPolicySettings { get; set; } = null!;
    public DbSet<DbHealthState> DbHealthStates { get; set; } = null!;
    public DbSet<RateLimitMetric> RateLimitMetrics { get; set; } = null!;
    public DbSet<OutboundDelivery> OutboundDeliveries { get; set; } = null!;

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
