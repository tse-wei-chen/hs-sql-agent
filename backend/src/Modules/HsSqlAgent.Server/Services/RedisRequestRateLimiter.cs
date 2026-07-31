using StackExchange.Redis;

namespace HsSqlAgent.Server.Services;

public enum RateLimiterFailureMode
{
    FailClosed,
    FailOpen
}

public sealed record RedisRequestRateLimiterOptions(
    RateLimiterFailureMode FailureMode,
    string KeyPrefix);

public sealed class RedisRequestRateLimiter(
    IConnectionMultiplexer connectionMultiplexer,
    RedisRequestRateLimiterOptions options,
    ILogger<RedisRequestRateLimiter> logger) : IRequestRateLimiter
{
    private const string AcquireScript = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
            ttl = tonumber(ARGV[1])
        end
        if count <= tonumber(ARGV[2]) then
            return { 1, ttl }
        end
        return { 0, ttl }
        """;

    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();
    private readonly RedisRequestRateLimiterOptions _options = options;
    private readonly ILogger<RedisRequestRateLimiter> _logger = logger;

    public async ValueTask<RateLimitResult> AcquireAsync(
        RateLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.PermitLimit <= 0 || request.Window <= TimeSpan.Zero)
            return RateLimitResult.Allowed;

        var windowMilliseconds = Math.Max(1L, (long)Math.Ceiling(request.Window.TotalMilliseconds));
        var key = (RedisKey)(
            $"{_options.KeyPrefix}{request.Partition}:{request.PermitLimit}:{windowMilliseconds}");

        try
        {
            var redisResult = await _database.ScriptEvaluateAsync(
                AcquireScript,
                [key],
                [windowMilliseconds, request.PermitLimit]);
            var values = (RedisResult[]?)redisResult;
            if (values is not { Length: >= 2 })
                throw new RedisException("Redis rate limiter returned an invalid result.");

            var isAllowed = (long)values[0] == 1;
            var retryAfterMilliseconds = Math.Max(0L, (long)values[1]);
            return new RateLimitResult(
                isAllowed,
                isAllowed ? TimeSpan.Zero : TimeSpan.FromMilliseconds(retryAfterMilliseconds));
        }
        catch (Exception exception) when (
            exception is RedisException or TimeoutException &&
            !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Redis rate limiter is unavailable.");
            return _options.FailureMode == RateLimiterFailureMode.FailOpen
                ? RateLimitResult.Allowed
                : RateLimitResult.Unavailable;
        }
    }
}
