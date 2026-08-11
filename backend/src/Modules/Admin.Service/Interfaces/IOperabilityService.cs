using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IOperabilityService
{
    Task<ExecutionMetricSummary> GetMetricsAsync(OperabilityFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<DbHealthItem>> GetDbHealthAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<KeyUsageItem>> GetKeyUsageAsync(OperabilityFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<DeliveryStatusItem>> GetDeliveriesAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<bool> RetryDeliveryAsync(long id, CancellationToken cancellationToken = default);
    Task<bool> QueueDeliveryAsync(string category, string dedupeKey, string targetUrl, string payload, CancellationToken cancellationToken = default);
}
