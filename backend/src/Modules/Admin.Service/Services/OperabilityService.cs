using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Admin.Service.Services;

public class OperabilityService(IAdminContext context, IOptions<OperabilitySettings> settings) : IOperabilityService
{
    private readonly OperabilitySettings _settings = settings.Value;

    public async Task<ExecutionMetricSummary> GetMetricsAsync(OperabilityFilter filter, CancellationToken cancellationToken = default)
    {
        var (from, to) = NormalizeRange(filter);
        var query = context.AuditLogs.AsNoTracking().Where(x =>
            x.CreatedAt >= from && x.CreatedAt <= to &&
            (x.Action == "mcp.query.executed" || x.Action == "mcp.dml.executed" ||
             x.Operation == "select" || x.Operation == "insert" || x.Operation == "update" || x.Operation == "delete"));
        if (filter.DbManagementId.HasValue) query = query.Where(x => x.DbManagementId == filter.DbManagementId);
        if (filter.AccessKeyId.HasValue) query = query.Where(x => x.AccessKeyId == filter.AccessKeyId);
        if (!string.IsNullOrWhiteSpace(filter.ToolName)) query = query.Where(x => x.ToolName == filter.ToolName);

        var queryCount = await query.LongCountAsync(x => x.Action == "mcp.query.executed" || x.Operation == "select", cancellationToken);
        var dmlCount = await query.LongCountAsync(x => x.Action == "mcp.dml.executed" || x.Operation == "insert" || x.Operation == "update" || x.Operation == "delete", cancellationToken);
        var total = await query.LongCountAsync(cancellationToken);
        var success = await query.LongCountAsync(x => x.Result == "success", cancellationToken);
        var slow = await query.LongCountAsync(x => x.DurationMs >= _settings.SlowQueryThresholdMs, cancellationToken);
        const int maxLatencySamples = 100_000;
        var durationValues = await query.Where(x => x.DurationMs.HasValue)
            .OrderByDescending(x => x.CreatedAt).Select(x => x.DurationMs!.Value)
            .Take(maxLatencySamples + 1).ToListAsync(cancellationToken);
        var sampled = durationValues.Count > maxLatencySamples;
        var durations = durationValues.Take(maxLatencySamples).Order().ToArray();
        var rateLimits = context.RateLimitMetrics.AsNoTracking().Where(x => x.BucketStart >= from && x.BucketStart <= to);
        if (filter.DbManagementId.HasValue) rateLimits = rateLimits.Where(x => x.DbManagementId == filter.DbManagementId);
        if (filter.AccessKeyId.HasValue) rateLimits = rateLimits.Where(x => x.AccessKeyId == filter.AccessKeyId);
        if (!string.IsNullOrWhiteSpace(filter.ToolName)) rateLimits = rateLimits.Where(x => x.ToolName == filter.ToolName);
        var limits = await rateLimits.GroupBy(x => x.Layer)
            .Select(x => new { Layer = x.Key, Count = x.Sum(y => y.RejectedCount) })
            .ToListAsync(cancellationToken);
        return new ExecutionMetricSummary
        {
            QueryCount = queryCount,
            DmlCount = dmlCount,
            SuccessCount = success,
            FailureCount = total - success,
            SuccessRate = total == 0 ? 0 : (double)success / total,
            P50LatencyMs = Percentile(durations, 0.50),
            P95LatencyMs = Percentile(durations, 0.95),
            LatencySampleSize = durations.Length,
            LatencySampled = sampled,
            SlowQueryCount = slow,
            IpRateLimitCount = limits.FirstOrDefault(x => x.Layer == "ip")?.Count ?? 0,
            KeyRateLimitCount = limits.FirstOrDefault(x => x.Layer == "key")?.Count ?? 0
        };
    }

    public async Task<IReadOnlyCollection<DbHealthItem>> GetDbHealthAsync(CancellationToken cancellationToken = default)
        => await context.DbManagement.AsNoTracking().GroupJoin(
            context.DbHealthStates.AsNoTracking(), db => db.Id, health => health.DbManagementId,
            (db, health) => new { db, health = health.FirstOrDefault() })
            .OrderBy(x => x.db.Name)
            .Select(x => new DbHealthItem
            {
                DbManagementId = x.db.Id, Name = x.db.Name, Provider = x.db.SqlProvider,
                Status = x.health == null ? "unknown" : x.health.Status,
                LastCheckedAt = x.health == null ? null : x.health.LastCheckedAt,
                LastSuccessAt = x.health == null ? null : x.health.LastSuccessAt,
                LatencyMs = x.health == null ? null : x.health.LatencyMs,
                ConsecutiveFailures = x.health == null ? 0 : x.health.ConsecutiveFailures,
                OutageStartedAt = x.health == null ? null : x.health.OutageStartedAt,
                LastError = x.health == null ? null : x.health.LastError
            }).ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<KeyUsageItem>> GetKeyUsageAsync(OperabilityFilter filter, CancellationToken cancellationToken = default)
    {
        var (from, to) = NormalizeRange(filter);
        var keys = context.McpAccessKeys.AsNoTracking().AsQueryable();
        if (filter.AccessKeyId.HasValue) keys = keys.Where(x => x.Id == filter.AccessKeyId);
        if (filter.DbManagementId.HasValue) keys = keys.Where(x => x.DbManagementId == filter.DbManagementId);
        var items = await keys.OrderBy(x => x.Name).Select(x => new KeyUsageItem
        {
            AccessKeyId = x.Id, Name = x.Name, LastUsedAt = x.LastUsedAt
        }).ToListAsync(cancellationToken);
        var ids = items.Select(x => x.AccessKeyId).ToArray();
        var usageQuery = context.AuditLogs.AsNoTracking()
            .Where(x => x.CreatedAt >= from && x.CreatedAt <= to && x.AccessKeyId.HasValue && ids.Contains(x.AccessKeyId.Value));
        if (filter.DbManagementId.HasValue) usageQuery = usageQuery.Where(x => x.DbManagementId == filter.DbManagementId);
        if (!string.IsNullOrWhiteSpace(filter.ToolName)) usageQuery = usageQuery.Where(x => x.ToolName == filter.ToolName);
        var usage = await usageQuery
            .GroupBy(x => x.AccessKeyId!.Value).Select(x => new
            {
                Id = x.Key,
                Count = x.LongCount(),
                Success = x.LongCount(y => y.Result == "success")
            }).ToDictionaryAsync(x => x.Id, cancellationToken);
        var rateLimitUsage = await context.RateLimitMetrics.AsNoTracking()
            .Where(x => x.Layer == "key" && x.BucketStart >= from && x.BucketStart <= to && x.AccessKeyId.HasValue && ids.Contains(x.AccessKeyId.Value))
            .GroupBy(x => x.AccessKeyId!.Value).Select(x => new
            {
                Id = x.Key,
                Attempts = x.Sum(y => y.AttemptCount),
                Rejected = x.Sum(y => y.RejectedCount)
            }).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var item in items)
        {
            var keyUsage = usage.GetValueOrDefault(item.AccessKeyId);
            item.RequestCount = keyUsage?.Count ?? 0;
            item.SuccessCount = keyUsage?.Success ?? 0;
            item.FailureCount = item.RequestCount - item.SuccessCount;
            var limitUsage = rateLimitUsage.GetValueOrDefault(item.AccessKeyId);
            item.RateLimitCount = limitUsage?.Rejected ?? 0;
            item.RateLimitRejectionRate = limitUsage is not { Attempts: > 0 }
                ? 0
                : (double)limitUsage.Rejected / limitUsage.Attempts;
        }
        return items;
    }

    public async Task<IReadOnlyCollection<DeliveryStatusItem>> GetDeliveriesAsync(int limit = 100, CancellationToken cancellationToken = default)
        => await context.OutboundDeliveries.AsNoTracking().OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500)).Select(x => new DeliveryStatusItem
            {
                Id = x.Id, Category = x.Category, Status = x.Status, AttemptCount = x.AttemptCount,
                CreatedAt = x.CreatedAt, DeliveredAt = x.DeliveredAt, LastAttemptAt = x.LastAttemptAt, LastError = x.LastError
            }).ToListAsync(cancellationToken);

    public async Task<bool> RetryDeliveryAsync(long id, CancellationToken cancellationToken = default)
    {
        var item = await context.OutboundDeliveries.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return false;
        item.Status = "pending"; item.AttemptCount = 0; item.NextAttemptAt = DateTime.UtcNow; item.LastError = null;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> QueueDeliveryAsync(string category, string dedupeKey, string targetUrl, string payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) return false;
        if (await context.OutboundDeliveries.AnyAsync(x => x.DedupeKey == dedupeKey, cancellationToken)) return false;
        context.OutboundDeliveries.Add(new OutboundDelivery
        {
            Category = category, DedupeKey = dedupeKey, TargetUrl = targetUrl, Payload = payload,
            Status = "pending", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow
        });
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return false; }
        return true;
    }

    private static (DateTime From, DateTime To) NormalizeRange(OperabilityFilter filter)
    {
        var to = filter.To ?? DateTime.UtcNow;
        var from = filter.From ?? to.AddDays(-1);
        if (from > to) throw new ArgumentException("From must not be later than To.");
        if (to - from > TimeSpan.FromDays(31)) throw new ArgumentException("Metrics range cannot exceed 31 days.");
        return (from, to);
    }

    private static double? Percentile(long[] values, double percentile)
    {
        if (values.Length == 0) return null;
        var position = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(position); var upper = (int)Math.Ceiling(position);
        return lower == upper ? values[lower] : values[lower] + (values[upper] - values[lower]) * (position - lower);
    }
}
