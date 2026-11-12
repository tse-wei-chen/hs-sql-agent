using HsSqlAgent.Server.Services;
using StackExchange.Redis;

namespace HsSqlAgent.Server.Extensions;

internal static class SqlConcurrencyServiceCollectionExtensions
{
    public static IServiceCollection AddSqlConcurrencyLimiter(
        this IServiceCollection services,
        string provider,
        string? connectionString,
        string failureMode,
        string key,
        int leaseSeconds)
    {
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISqlExecutionConcurrencyLimiter, SqlExecutionConcurrencyLimiter>();
            return services;
        }

        if (!string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported SQL concurrency provider '{provider}'. Supported providers: Memory, Redis.");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "SqlConcurrency connection string is required when provider is Redis.");
        if (!Enum.TryParse<RateLimiterFailureMode>(failureMode, true, out var parsedFailureMode))
            throw new InvalidOperationException(
                $"Unsupported SQL concurrency failure mode '{failureMode}'. Supported modes: FailClosed, FailOpen.");
        // SQL concurrency is an admission/safety boundary. If Redis is unavailable,
        // allowing an unlimited fallback lease defeats MaxConcurrentSql. Keep the
        // failure-mode enum for compatibility with independent rate limiting,
        // but never register an unfenced SQL concurrency limiter.
        if (parsedFailureMode != RateLimiterFailureMode.FailClosed)
            throw new InvalidOperationException(
                "Redis SQL concurrency requires FailClosed; FailOpen is unsupported because it bypasses MaxConcurrentSql.");
        // A TTL sorted-set lease is not a consensus-fenced token. Redis
        // Sentinel or multi-primary/Cluster failover can lose an acknowledged
        // lease during asynchronous promotion, making two independent SQL
        // operations believe they both own MaxConcurrentSql=1. Refuse those
        // explicitly configured topologies rather than advertising a strong
        // distributed guarantee that this limiter cannot establish.
        ConfigurationOptions parsedConnection;
        try
        {
            parsedConnection = ConfigurationOptions.Parse(connectionString);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "SqlConcurrency Redis connection string is invalid.", error);
        }
        if (!string.IsNullOrWhiteSpace(parsedConnection.ServiceName)
            || parsedConnection.EndPoints.Count != 1)
            throw new InvalidOperationException(
                "Redis Sentinel, Cluster or multi-endpoint failover is unsupported for SQL concurrency leases; use one authoritative standalone Redis primary, or a separate quorum-fenced coordinator.");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("SqlConcurrency key is required.");
        if (leaseSeconds < 2)
            throw new InvalidOperationException("SqlConcurrency lease must be at least two seconds.");

        services.AddSingleton(new RedisSqlConcurrencyOptions(
            connectionString,
            key,
            TimeSpan.FromSeconds(leaseSeconds),
            parsedFailureMode));
        services.AddSingleton<ISqlExecutionConcurrencyLimiter, RedisSqlExecutionConcurrencyLimiter>();
        return services;
    }
}
