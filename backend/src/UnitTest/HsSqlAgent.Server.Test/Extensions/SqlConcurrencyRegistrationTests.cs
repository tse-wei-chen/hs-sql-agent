using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class SqlConcurrencyRegistrationTests
{
    [Fact]
    public void AddHsSqlAgent_ShouldRegisterMemorySqlConcurrencyByDefault()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgent(CreateOptions());

        var registration = Assert.Single(
            services,
            x => x.ServiceType == typeof(ISqlExecutionConcurrencyLimiter));
        Assert.Equal(typeof(SqlExecutionConcurrencyLimiter), registration.ImplementationType);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRegisterRedisSqlConcurrency()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();
        options.SqlConcurrencyProvider = "Redis";
        options.SqlConcurrencyConnectionString = "localhost:6379";

        services.AddHsSqlAgent(options);

        var registration = Assert.Single(
            services,
            x => x.ServiceType == typeof(ISqlExecutionConcurrencyLimiter));
        Assert.Equal(typeof(RedisSqlExecutionConcurrencyLimiter), registration.ImplementationType);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRejectRedisSqlConcurrencyWithoutConnectionString()
    {
        var options = CreateOptions();
        options.SqlConcurrencyProvider = "Redis";

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
