using HsSqlAgent.Server.Services;
using StackExchange.Redis;

namespace HsSqlAgent.Server.Extensions;

internal static class RequestRateLimiterServiceCollectionExtensions
{
    public static IServiceCollection AddRequestRateLimiter(
        this IServiceCollection services,
        string provider,
        string? connectionString,
        string failureMode,
        string keyPrefix)
    {
        services.AddSingleton(TimeProvider.System);

        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IRequestRateLimiter, MemoryRequestRateLimiter>();
            return services;
        }

        if (!string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported rate limiter provider '{provider}'. Supported providers: Memory, Redis.");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "RateLimiter connection string is required when provider is Redis.");
        if (!Enum.TryParse<RateLimiterFailureMode>(failureMode, true, out var parsedFailureMode))
            throw new InvalidOperationException(
                $"Unsupported rate limiter failure mode '{failureMode}'. Supported modes: FailClosed, FailOpen.");
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new InvalidOperationException("RateLimiter key prefix is required.");

        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(connectionString));
        services.AddSingleton(new RedisRequestRateLimiterOptions(parsedFailureMode, keyPrefix));
        services.AddSingleton<IRequestRateLimiter, RedisRequestRateLimiter>();
        return services;
    }
}
