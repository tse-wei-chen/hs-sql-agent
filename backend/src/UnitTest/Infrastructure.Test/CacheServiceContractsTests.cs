using Common.Interfaces;
using Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.Test;

public class CacheServiceContractsTests
{
    [Fact]
    public async Task MemoryCache_RoundTripsAndRemovesValues()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new MemoryCacheService(cache);
        Assert.Null(await service.GetAsync<string>("missing"));

        await service.SetAsync("request:1", "opaque-value");
        Assert.Equal("opaque-value", await service.GetAsync<string>("request:1"));

        await service.RemoveAsync("request:1");
        Assert.Null(await service.GetAsync<string>("request:1"));
    }

    [Fact]
    public async Task MemoryCache_RespectsAbsoluteExpiration()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new MemoryCacheService(cache);

        await service.SetAsync("short-lived", "cannot-be-reused", TimeSpan.Zero);

        Assert.Null(await service.GetAsync<string>("short-lived"));
    }

    [Theory]
    [InlineData("Memory")]
    [InlineData("memory")]
    [InlineData("IMemoryCache")]
    public async Task MemoryProvider_RegistersUsableCacheWithoutRedis(string name)
    {
        var services = new ServiceCollection();
        services.AddCacheProvider(name, null);

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        Assert.IsType<MemoryCacheService>(cache);
        await cache.SetAsync("key", "cached");
        Assert.Equal("cached", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public void RedisProvider_RejectsMissingConfigurationWithoutOpeningAConnection()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(
            () => services.AddCacheProvider("Redis", null));
        Assert.Throws<InvalidOperationException>(
            () => services.AddCacheProvider("Redis", "localhost:6379", ""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Memroy")]
    [InlineData("Unknown")]
    public void UnknownProvider_FailsClosedWithoutSilentMemoryFallback(string provider)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddCacheProvider(provider, null));
        Assert.Contains("Unsupported cache provider", error.Message);
    }
}
