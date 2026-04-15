using Microsoft.EntityFrameworkCore;
using Admin.Service.Data.Entites;

namespace Admin.Service.Data;

public interface IAdminContext
{
    DbSet<SuperUser> SuperUsers { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<McpAccessKey> McpAccessKeys { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
