using Microsoft.EntityFrameworkCore;
using Modules.Data.Entites;

namespace Modules.Data;

public interface IAdminContext
{
    DbSet<SuperUser> SuperUsers { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<McpAccessKey> McpAccessKeys { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}