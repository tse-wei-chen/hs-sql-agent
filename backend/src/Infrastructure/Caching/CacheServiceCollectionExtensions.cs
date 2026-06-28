using Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Caching;

public static class CacheServiceCollectionExtensions
{
    public static IServiceCollection AddCacheProvider(this IServiceCollection services, string provider, string? connectionString)
    {
        if (string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("CacheConnectionString is required when CacheProvider is Redis.");

            services.AddStackExchangeRedisCache(options => options.Configuration = connectionString);
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        else
        {
            services.AddMemoryCache();
            services.AddSingleton<ICacheService, MemoryCacheService>();
        }

        return services;
    }
}
