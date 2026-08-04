using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IAuditService
{
    Task WriteAsync(string action, string target, string result, string? detail = null, string? actorType = null, string? actorId = null, string? ipAddress = null, string? userAgent = null, CancellationToken cancellationToken = default);
    Task WriteLogAsync(string action, string target, string result, string? detail = null, CancellationToken cancellationToken = default);
    Task WriteEventAsync(
        string action,
        string target,
        string result,
        AuditEventContext eventContext,
        string? detail = null,
        CancellationToken cancellationToken = default);
    Task<AuditLogQueryResult> QueryAsync(int page, int pageSize, string? action = null, string? keyword = null, CancellationToken cancellationToken = default);
    Task<AuditLogQueryResult> QueryAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<AuditDailySummaryItem>> QueryDailySummaryAsync(int days, string? action = null, string? keyword = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<AuditLogItem>> ExportAsync(AuditLogFilter filter, int maxRows = 100_000, CancellationToken cancellationToken = default);
}
