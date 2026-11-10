using Admin.Service.Interfaces;
using StackExchange.Redis;

namespace HsSqlAgent.Server.Services;

public sealed record RedisSqlConcurrencyOptions(
    string ConnectionString,
    string Key,
    TimeSpan LeaseDuration,
    RateLimiterFailureMode FailureMode);

public sealed class RedisSqlExecutionConcurrencyLimiter :
    ISqlExecutionConcurrencyLimiter,
    IAsyncDisposable
{
    private const string AcquireScript = """
        local time = redis.call('TIME')
        local now = (time[1] * 1000) + math.floor(time[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[1]) then
            return 0
        end
        redis.call('ZADD', KEYS[1], now + tonumber(ARGV[2]), ARGV[3])
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]) * 2)
        return 1
        """;

    private const string RenewScript = """
        local score = redis.call('ZSCORE', KEYS[1], ARGV[2])
        if not score then
            return 0
        end
        local time = redis.call('TIME')
        local now = (time[1] * 1000) + math.floor(time[2] / 1000)
        -- A stale member remains in the sorted set until the next acquire.
        -- Its existence is not proof of a valid lease: never revive it.
        if tonumber(score) <= now then
            redis.call('ZREM', KEYS[1], ARGV[2])
            return 0
        end
        redis.call('ZADD', KEYS[1], 'XX', now + tonumber(ARGV[1]), ARGV[2])
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[1]) * 2)
        return 1
        """;

    private const string ReleaseScript = """
        return redis.call('ZREM', KEYS[1], ARGV[1])
        """;

    private readonly ISecurityPolicyRuntimeState _runtimeState;
    private readonly RedisSqlConcurrencyOptions _options;
    private readonly ILogger<RedisSqlExecutionConcurrencyLimiter> _logger;
    private readonly Lazy<Task<ConnectionMultiplexer>>? _connection;
    private readonly IDatabase? _database;

    public RedisSqlExecutionConcurrencyLimiter(
        ISecurityPolicyRuntimeState runtimeState,
        RedisSqlConcurrencyOptions options,
        ILogger<RedisSqlExecutionConcurrencyLimiter> logger)
    {
        _runtimeState = runtimeState;
        _options = options;
        _logger = logger;
        _connection = new Lazy<Task<ConnectionMultiplexer>>(ConnectAsync);
    }

    internal RedisSqlExecutionConcurrencyLimiter(
        ISecurityPolicyRuntimeState runtimeState,
        RedisSqlConcurrencyOptions options,
        ILogger<RedisSqlExecutionConcurrencyLimiter> logger,
        IDatabase database)
    {
        _runtimeState = runtimeState;
        _options = options;
        _logger = logger;
        _database = database;
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maximum = _runtimeState.GetCurrent().MaxConcurrentSql;
        if (maximum <= 0)
            return NoOpLease.Instance;

        var token = Guid.NewGuid().ToString("N");
        var leaseMilliseconds = Math.Max(1000L, (long)_options.LeaseDuration.TotalMilliseconds);

        try
        {
            var database = await GetDatabaseAsync(cancellationToken);
            var result = await database.ScriptEvaluateAsync(
                AcquireScript,
                [_options.Key],
                [maximum, leaseMilliseconds, token]);
            var acquired = (long)result == 1;

            // The Redis command cannot take a CancellationToken. A request may
            // be canceled while the Lua script is in flight, after it already
            // reserved a distributed slot. Never return that late admission:
            // atomically release this request's exact token first.
            if (cancellationToken.IsCancellationRequested)
            {
                if (acquired)
                    await ReleaseAsync(token);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!acquired)
                return null;

            return new RedisLease(this, token, _options.LeaseDuration);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            // Redis failure modes apply only to active requests. Cancellation
            // must not be converted into FailOpen admission (or a Redis error).
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogError(exception, "Redis SQL concurrency limiter is unavailable.");
            return _options.FailureMode == RateLimiterFailureMode.FailOpen
                ? NoOpLease.Instance
                : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { IsValueCreated: true } && _connection.Value.IsCompletedSuccessfully)
        {
            var connection = await _connection.Value;
            await connection.DisposeAsync();
        }
    }

    private Task<ConnectionMultiplexer> ConnectAsync()
    {
        var configuration = ConfigurationOptions.Parse(_options.ConnectionString);
        configuration.AbortOnConnectFail = false;
        return ConnectionMultiplexer.ConnectAsync(configuration);
    }

    private async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_database is not null)
            return _database;

        var connection = await _connection!.Value.WaitAsync(cancellationToken);
        return connection.GetDatabase();
    }

    private async Task<bool> RenewAsync(string token)
    {
        try
        {
            var leaseMilliseconds = Math.Max(1000L, (long)_options.LeaseDuration.TotalMilliseconds);
            var database = await GetDatabaseAsync();
            var result = await database.ScriptEvaluateAsync(
                RenewScript,
                [_options.Key],
                [leaseMilliseconds, token]);
            return (long)result == 1;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to renew a distributed SQL concurrency lease.");
            return false;
        }
    }

    private async Task ReleaseAsync(string token)
    {
        try
        {
            var database = await GetDatabaseAsync();
            await database.ScriptEvaluateAsync(
                ReleaseScript,
                [_options.Key],
                [token]);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to release a distributed SQL concurrency lease.");
        }
    }

    private sealed class RedisLease : IRevocableSqlExecutionLease
    {
        private readonly RedisSqlExecutionConcurrencyLimiter _owner;
        private readonly string _token;
        private readonly TimeSpan _leaseDuration;
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _revoked = new();
        private readonly Task _renewalTask;
        private int _disposed;

        public CancellationToken RevocationToken => _revoked.Token;

        public RedisLease(
            RedisSqlExecutionConcurrencyLimiter owner,
            string token,
            TimeSpan leaseDuration)
        {
            _owner = owner;
            _token = token;
            _leaseDuration = leaseDuration;
            // A pending Redis call must never keep an unverified lease alive
            // beyond its local deadline. The watchdog also covers lost replies.
            _revoked.CancelAfter(leaseDuration);
            _renewalTask = RenewUntilDisposedAsync(
                TimeSpan.FromMilliseconds(Math.Max(500, leaseDuration.TotalMilliseconds / 2)));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _stopping.CancelAsync();
            try
            {
                await _renewalTask;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            finally
            {
                _stopping.Dispose();
                _revoked.Dispose();
                await _owner.ReleaseAsync(_token);
            }
        }

        private async Task RenewUntilDisposedAsync(TimeSpan interval)
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                if (!await _owner.RenewAsync(_token))
                {
                    // Missing, expired, or unverified due to Redis outage:
                    // request cancellation of any still-running SQL Query.
                    _revoked.Cancel();
                    return;
                }

                // CancelAfter never un-cancels a token. A late response
                // cannot restore a local deadline that already elapsed.
                _revoked.CancelAfter(_leaseDuration);
            }
        }
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static NoOpLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
