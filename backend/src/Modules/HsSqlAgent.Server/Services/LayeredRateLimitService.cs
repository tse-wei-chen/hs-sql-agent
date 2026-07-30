using System.Collections.Concurrent;
using Admin.Service.Interfaces;
using Admin.Service.Models;

namespace HsSqlAgent.Server.Services;

public interface ILayeredRateLimitService
{
    bool TryAcquireKey(
        int keyId,
        McpKeyRateLimitMode mode,
        int? permitLimitOverride,
        int? windowSecondsOverride,
        out TimeSpan retryAfter);
}

public sealed class LayeredRateLimitService(
    ISecurityPolicyRuntimeState securityPolicyRuntimeState) : ILayeredRateLimitService
{
    private sealed class WindowCounter
    {
        public readonly Lock Sync = new();
        public DateTime WindowStartedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
        public int Limit { get; set; }
        public int WindowSeconds { get; set; }
    }

    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly ConcurrentDictionary<string, WindowCounter> _counters = new();
    private long _requestCount;

    public bool TryAcquireKey(
        int keyId,
        McpKeyRateLimitMode mode,
        int? permitLimitOverride,
        int? windowSecondsOverride,
        out TimeSpan retryAfter)
    {
        if (mode == McpKeyRateLimitMode.Unlimited)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        var policy = _securityPolicyRuntimeState.GetCurrent();
        var permitLimit = mode == McpKeyRateLimitMode.Custom
            ? permitLimitOverride ?? policy.KeyPermitLimit
            : policy.KeyPermitLimit;
        var windowSeconds = mode == McpKeyRateLimitMode.Custom
            ? windowSecondsOverride ?? policy.KeyWindowSeconds
            : policy.KeyWindowSeconds;
        return TryAcquire(
            $"key:{keyId}",
            permitLimit,
            windowSeconds,
            out retryAfter);
    }

    private bool TryAcquire(string partition, int limit, int windowSeconds, out TimeSpan retryAfter)
    {
        var now = DateTime.UtcNow;
        var counter = _counters.GetOrAdd(partition, _ => new WindowCounter
        {
            Limit = limit,
            WindowSeconds = windowSeconds,
            WindowStartedAt = now,
            LastSeenAt = now
        });

        lock (counter.Sync)
        {
            if (counter.Limit != limit || counter.WindowSeconds != windowSeconds ||
                now - counter.WindowStartedAt >= TimeSpan.FromSeconds(windowSeconds))
            {
                counter.Limit = limit;
                counter.WindowSeconds = windowSeconds;
                counter.WindowStartedAt = now;
                counter.Count = 0;
            }

            counter.LastSeenAt = now;
            if (counter.Count >= limit)
            {
                retryAfter = TimeSpan.FromSeconds(windowSeconds) - (now - counter.WindowStartedAt);
                if (retryAfter < TimeSpan.Zero)
                    retryAfter = TimeSpan.Zero;
                return false;
            }

            counter.Count++;
            retryAfter = TimeSpan.Zero;
        }

        if (Interlocked.Increment(ref _requestCount) % 1024 == 0)
            RemoveStaleCounters(now);
        return true;
    }

    private void RemoveStaleCounters(DateTime now)
    {
        foreach (var item in _counters)
        {
            var staleAfter = TimeSpan.FromSeconds(Math.Max(item.Value.WindowSeconds * 2, 120));
            if (now - item.Value.LastSeenAt > staleAfter)
                _counters.TryRemove(item.Key, out _);
        }
    }
}
