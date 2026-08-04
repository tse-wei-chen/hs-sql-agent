using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IAuditRetentionService
{
    AuditRetentionPolicy GetPolicy();
    Task<AuditRetentionResult> ExecuteAsync(bool dryRun, CancellationToken cancellationToken = default);
}
