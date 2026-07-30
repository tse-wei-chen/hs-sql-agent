using Microsoft.EntityFrameworkCore;
using Admin.Service.Data.Entites;

namespace Admin.Service.Data;

public interface IAdminContext
{
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<McpAccessKey> McpAccessKeys { get; }
    DbSet<CustomSqlTool> CustomSqlTools { get; }
    DbSet<DbManagement> DbManagement { get; }
    DbSet<DbSemantic> DbSemantics { get; }
    DbSet<SecurityPolicySettings> SecurityPolicySettings { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
