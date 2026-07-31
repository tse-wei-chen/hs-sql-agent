using System.Collections.Concurrent;

namespace HsSqlAgent.Server.Services;

public sealed record RateLimitRequest(
    string Partition,
    int PermitLimit,
    TimeSpan Window);

public readonly record struct RateLimitResult(
    bool IsAllowed,
    TimeSpan RetryAfter,
    bool IsAvailable = true)
{
    public static RateLimitResult Allowed => new(true, TimeSpan.Zero);
    public static RateLimitResult Unavailable => new(false, TimeSpan.Zero, false);
}

public interface IRequestRateLimiter
{
    ValueTask<RateLimitResult> AcquireAsync(
        RateLimitRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MemoryRequestRateLimiter : IRequestRateLimiter
{
    private sealed class WindowCounter
    {
        public readonly Lock Sync = new();
        public DateTimeOffset WindowStartedAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public int Count { get; set; }
        public int Limit { get; set; }
        public TimeSpan Window { get; set; }
    }

    private readonly ConcurrentDictionary<string, WindowCounter> _counters = new();
    private readonly TimeProvider _timeProvider;
    private long _requestCount;

    public MemoryRequestRateLimiter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ValueTask<RateLimitResult> AcquireAsync(
        RateLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.PermitLimit <= 0 || request.Window <= TimeSpan.Zero)
            return ValueTask.FromResult(RateLimitResult.Allowed);

        var now = _timeProvider.GetUtcNow();
        var counter = _counters.GetOrAdd(request.Partition, _ => new WindowCounter
        {
            Limit = request.PermitLimit,
            Window = request.Window,
            WindowStartedAt = now,
            LastSeenAt = now
        });

        RateLimitResult result;
        lock (counter.Sync)
        {
            if (counter.Limit != request.PermitLimit ||
                counter.Window != request.Window ||
                now - counter.WindowStartedAt >= request.Window)
            {
                counter.Limit = request.PermitLimit;
                counter.Window = request.Window;
                counter.WindowStartedAt = now;
                counter.Count = 0;
            }

            counter.LastSeenAt = now;
            if (counter.Count >= request.PermitLimit)
            {
                var retryAfter = request.Window - (now - counter.WindowStartedAt);
                result = new RateLimitResult(false, retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero);
            }
            else
            {
                counter.Count++;
                result = RateLimitResult.Allowed;
            }
        }

        if (Interlocked.Increment(ref _requestCount) % 1024 == 0)
            RemoveStaleCounters(now);

        return ValueTask.FromResult(result);
    }

    private void RemoveStaleCounters(DateTimeOffset now)
    {
        foreach (var item in _counters)
        {
            var staleAfter = item.Value.Window > TimeSpan.FromMinutes(2)
                ? item.Value.Window * 2
                : TimeSpan.FromMinutes(2);
            if (now - item.Value.LastSeenAt > staleAfter)
                _counters.TryRemove(item.Key, out _);
        }
    }
}
