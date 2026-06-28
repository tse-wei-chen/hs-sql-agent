using System.Text.Json;
using Common.Interfaces;
using Microsoft.Extensions.Caching.Distributed;

namespace Infrastructure.Caching;

public class RedisCacheService(IDistributedCache distributedCache) : ICacheService
{
    private readonly IDistributedCache _distributedCache = distributedCache;

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var json = await _distributedCache.GetStringAsync(key, cancellationToken);
        return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpireTime = null, CancellationToken cancellationToken = default)
    {
        var options = new DistributedCacheEntryOptions();
        if (absoluteExpireTime.HasValue)
            options.AbsoluteExpirationRelativeToNow = absoluteExpireTime;

        var json = JsonSerializer.Serialize(value);
        await _distributedCache.SetStringAsync(key, json, options, cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await _distributedCache.RemoveAsync(key, cancellationToken);
    }
}
