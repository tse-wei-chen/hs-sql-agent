using Admin.Service.Data.Entites;

namespace Admin.Service.Interfaces;

public interface IAuditQueue
{
    bool TryEnqueue(AuditLog log);
    IAsyncEnumerable<AuditLog> DequeueAllAsync(CancellationToken cancellationToken);
}
