using System.Collections.Concurrent;

namespace HsSqlAgent.Server.Services;

public interface IOperationalMetricRecorder
{
    void RecordRateLimit(string layer, int? accessKeyId = null, int? dbManagementId = null, string? toolName = null);
    void RecordRateLimitAttempt(string layer, bool rejected, int? accessKeyId = null, int? dbManagementId = null, string? toolName = null);
    IReadOnlyCollection<RateLimitMetricSnapshot> Drain();
    void Restore(IEnumerable<RateLimitMetricSnapshot> snapshots);
}

public readonly record struct RateLimitMetricSnapshot(
    DateTime BucketStart, string Layer, int? AccessKeyId, int? DbManagementId, string? ToolName,
    long AttemptCount, long RejectedCount);

public class OperationalMetricRecorder(IHsSqlAgentMetrics? prometheusMetrics = null) : IOperationalMetricRecorder
{
    private readonly ConcurrentDictionary<MetricKey, MetricCounts> _counts = new();

    public void RecordRateLimit(string layer, int? accessKeyId = null, int? dbManagementId = null, string? toolName = null)
        => RecordRateLimitAttempt(layer, true, accessKeyId, dbManagementId, toolName);

    public void RecordRateLimitAttempt(string layer, bool rejected, int? accessKeyId = null, int? dbManagementId = null, string? toolName = null)
    {
        if (rejected) prometheusMetrics?.RecordRateLimitRejection(layer);
        var now = DateTime.UtcNow;
        var bucket = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        _counts.AddOrUpdate(
            new MetricKey(bucket, layer, accessKeyId, dbManagementId, toolName),
            new MetricCounts(1, rejected ? 1 : 0),
            (_, value) => new MetricCounts(value.AttemptCount + 1, value.RejectedCount + (rejected ? 1 : 0)));
    }

    public IReadOnlyCollection<RateLimitMetricSnapshot> Drain()
    {
        var result = new List<RateLimitMetricSnapshot>();
        foreach (var pair in _counts.ToArray())
            if (_counts.TryRemove(pair.Key, out var count))
                result.Add(new(pair.Key.BucketStart, pair.Key.Layer, pair.Key.AccessKeyId, pair.Key.DbManagementId, pair.Key.ToolName,
                    count.AttemptCount, count.RejectedCount));
        return result;
    }

    public void Restore(IEnumerable<RateLimitMetricSnapshot> snapshots)
    {
        foreach (var item in snapshots)
            _counts.AddOrUpdate(
                new MetricKey(item.BucketStart, item.Layer, item.AccessKeyId, item.DbManagementId, item.ToolName),
                new MetricCounts(item.AttemptCount, item.RejectedCount),
                (_, value) => new MetricCounts(
                    value.AttemptCount + item.AttemptCount,
                    value.RejectedCount + item.RejectedCount));
    }

    private readonly record struct MetricKey(DateTime BucketStart, string Layer, int? AccessKeyId, int? DbManagementId, string? ToolName);
    private readonly record struct MetricCounts(long AttemptCount, long RejectedCount);
}
