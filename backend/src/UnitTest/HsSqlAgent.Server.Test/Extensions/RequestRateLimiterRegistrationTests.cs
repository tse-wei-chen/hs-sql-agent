using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class RequestRateLimiterRegistrationTests
{
    [Fact]
    public void AddHsSqlAgent_ShouldRegisterMemoryProviderByDefault()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgent(CreateOptions());

        var registration = Assert.Single(
            services,
            x => x.ServiceType == typeof(IRequestRateLimiter));
        Assert.Equal(typeof(MemoryRequestRateLimiter), registration.ImplementationType);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRegisterRedisProvider()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();
        options.RateLimiterProvider = "Redis";
        options.RateLimiterConnectionString = "localhost:6379";

        services.AddHsSqlAgent(options);

        var registration = Assert.Single(
            services,
            x => x.ServiceType == typeof(IRequestRateLimiter));
        Assert.Equal(typeof(RedisRequestRateLimiter), registration.ImplementationType);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRejectRedisWithoutConnectionString()
    {
        var options = CreateOptions();
        options.RateLimiterProvider = "Redis";

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHsSqlAgent(options));

        Assert.Contains("connection string is required", exception.Message);
    }

    private static HsSqlAgentServiceOptions CreateOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };
}
