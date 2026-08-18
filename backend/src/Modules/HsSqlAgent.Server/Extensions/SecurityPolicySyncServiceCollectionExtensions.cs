using Admin.Service.Interfaces;
using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Extensions;

internal static class SecurityPolicySyncServiceCollectionExtensions
{
    public static IServiceCollection AddSecurityPolicySync(
        this IServiceCollection services,
        string provider,
        string? connectionString,
        string keyPrefix,
        int refreshIntervalSeconds)
    {
        if (refreshIntervalSeconds <= 0)
            throw new InvalidOperationException("SecurityPolicySync refresh interval must be greater than zero.");

        services.AddScoped<SecurityPolicyDatabaseSynchronizer>();

        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISecurityPolicyChangePublisher, NoOpSecurityPolicyChangePublisher>();
            return services;
        }

        if (!string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported security policy sync provider '{provider}'. Supported providers: Memory, Redis.");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "SecurityPolicySync connection string is required when provider is Redis.");
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new InvalidOperationException("SecurityPolicySync key prefix is required.");

        services.AddSingleton(new RedisSecurityPolicySyncOptions(
            connectionString,
            $"{keyPrefix}current",
            $"{keyPrefix}changed"));
        services.AddSingleton<RedisSecurityPolicyChangeBus>();
        services.AddSingleton<ISecurityPolicyChangePublisher>(
            sp => sp.GetRequiredService<RedisSecurityPolicyChangeBus>());
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<RedisSecurityPolicyChangeBus>());
        return services;
    }
}
