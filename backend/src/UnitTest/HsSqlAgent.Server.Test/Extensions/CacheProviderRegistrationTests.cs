using Common.Interfaces;
using Infrastructure.Caching;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class CacheProviderRegistrationTests
{
    [Theory]
    [InlineData("Memory")]
    [InlineData("IMemoryCache")]
    public void AddCacheProvider_ShouldRegisterMemoryProvider(string providerName)
    {
        var services = new ServiceCollection();

        services.AddCacheProvider(providerName, null, "hsqlagent:cache:");

        using var provider = services.BuildServiceProvider();
        Assert.IsType<MemoryCacheService>(provider.GetRequiredService<ICacheService>());
    }

    [Fact]
    public void AddCacheProvider_ShouldConfigureRedisKeyPrefix()
    {
        var services = new ServiceCollection();

        services.AddCacheProvider("Redis", "localhost:6379", "hsqlagent:cache:");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        Assert.Equal("localhost:6379", options.Configuration);
        Assert.Equal("hsqlagent:cache:", options.InstanceName);
        Assert.IsType<RedisCacheService>(provider.GetRequiredService<ICacheService>());
    }

    [Fact]
    public void AddCacheProvider_ShouldRejectRedisWithoutKeyPrefix()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCacheProvider("Redis", "localhost:6379", string.Empty));

        Assert.Contains("CacheKeyPrefix is required", exception.Message);
    }

    [Fact]
    public void AddCacheProvider_ShouldRejectUnsupportedProvider()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCacheProvider("Unknown", null, "hsqlagent:cache:"));

        Assert.Contains("Unsupported cache provider", exception.Message);
    }
}
