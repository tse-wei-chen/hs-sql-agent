using HsSqlAgent.Server.Services;

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
