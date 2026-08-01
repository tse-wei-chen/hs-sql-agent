using Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Caching;

public static class CacheServiceCollectionExtensions
{
    public static IServiceCollection AddCacheProvider(
        this IServiceCollection services,
        string provider,
        string? connectionString) =>
        services.AddCacheProvider(provider, connectionString, "hsqlagent:cache:");

    public static IServiceCollection AddCacheProvider(
        this IServiceCollection services,
        string provider,
        string? connectionString,
        string keyPrefix)
    {
        if (string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("CacheConnectionString is required when CacheProvider is Redis.");
            if (string.IsNullOrWhiteSpace(keyPrefix))
                throw new InvalidOperationException("CacheKeyPrefix is required when CacheProvider is Redis.");

            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = connectionString;
                options.InstanceName = keyPrefix;
            });
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        else if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(provider, "IMemoryCache", StringComparison.OrdinalIgnoreCase))
        {
            services.AddMemoryCache();
            services.AddSingleton<ICacheService, MemoryCacheService>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported cache provider '{provider}'. Supported providers: Memory, Redis.");
        }

        return services;
    }
}
