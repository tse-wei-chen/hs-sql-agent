using System.Collections.Concurrent;

namespace HsSqlAgent.Server.Services;

public interface IOperationalMetricRecorder
{
    void RecordRateLimit(string layer, int? accessKeyId = null, int? dbManagementId = null, string? toolName = null);
    IReadOnlyCollection<RateLimitMetricSnapshot> Drain();
    void Restore(IEnumerable<RateLimitMetricSnapshot> snapshots);
}

public readonly record struct RateLimitMetricSnapshot(
    DateTime BucketStart, string Layer, int? AccessKeyId, int? DbManagementId, string? ToolName, long Count);

public class OperationalMetricRecorder : IOperationalMetricRecorder
{
    private readonly ConcurrentDictionary<MetricKey, long> _counts = new();

    public void RecordRateLimit(string layer, int? accessKeyId = null, int? dbManagementId = null, string? toolName = null)
    {
        var now = DateTime.UtcNow;
        var bucket = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        _counts.AddOrUpdate(new MetricKey(bucket, layer, accessKeyId, dbManagementId, toolName), 1, (_, value) => value + 1);
    }

    public IReadOnlyCollection<RateLimitMetricSnapshot> Drain()
    {
        var result = new List<RateLimitMetricSnapshot>();
        foreach (var pair in _counts.ToArray())
            if (_counts.TryRemove(pair.Key, out var count))
                result.Add(new(pair.Key.BucketStart, pair.Key.Layer, pair.Key.AccessKeyId, pair.Key.DbManagementId, pair.Key.ToolName, count));
        return result;
    }

    public void Restore(IEnumerable<RateLimitMetricSnapshot> snapshots)
    {
        foreach (var item in snapshots)
            _counts.AddOrUpdate(
                new MetricKey(item.BucketStart, item.Layer, item.AccessKeyId, item.DbManagementId, item.ToolName),
                item.Count, (_, value) => value + item.Count);
    }

    private readonly record struct MetricKey(DateTime BucketStart, string Layer, int? AccessKeyId, int? DbManagementId, string? ToolName);
}
