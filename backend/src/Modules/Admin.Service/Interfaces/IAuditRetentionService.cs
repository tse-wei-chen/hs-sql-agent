using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IAuditRetentionService
{
    Task<AuditRetentionResult> ExecuteAsync(bool dryRun, CancellationToken cancellationToken = default);
}
