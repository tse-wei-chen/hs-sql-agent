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
        Assert.Null(await service.GetAsync<string>("missing", TestContext.Current.CancellationToken));

        await service.SetAsync("request:1", "opaque-value", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("opaque-value", await service.GetAsync<string>("request:1", TestContext.Current.CancellationToken));

        await service.RemoveAsync("request:1", TestContext.Current.CancellationToken);
        Assert.Null(await service.GetAsync<string>("request:1"));
    }

    [Fact]
    public async Task MemoryCache_RejectsNonPositiveExpirationAndSupportsPositiveTtl()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new MemoryCacheService(cache);
        var cancellation = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.SetAsync("invalid-ttl", "never-cached", TimeSpan.Zero, cancellation));
        Assert.Null(await service.GetAsync<string>("invalid-ttl", cancellation));

        await service.SetAsync("valid-ttl", "short-lived", TimeSpan.FromMinutes(1), cancellation);
        Assert.Equal("short-lived", await service.GetAsync<string>("valid-ttl", cancellation));
    }

    [Theory]
    [InlineData("Memory")]
    [InlineData("memory")]
    [InlineData("IMemoryCache")]
    public void MemoryProvider_RegistersItsSingletonAdapterWithoutRedis(string name)
    {
        var services = new ServiceCollection();
        services.AddCacheProvider(name, null);

        var registration = Assert.Single(services, service => service.ServiceType == typeof(ICacheService));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        Assert.Equal(typeof(MemoryCacheService), registration.ImplementationType);
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
