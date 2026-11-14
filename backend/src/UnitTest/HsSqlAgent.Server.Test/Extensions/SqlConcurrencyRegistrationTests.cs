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

    [Fact]
    public void AddHsSqlAgent_RejectsRedisFailOpenBecauseItBypassesSqlAdmission()
    {
        var options = CreateOptions();
        options.SqlConcurrencyProvider = "Redis";
        options.SqlConcurrencyConnectionString = "localhost:6379";
        options.SqlConcurrencyFailureMode = "FailOpen";

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHsSqlAgent(options));
        Assert.Contains("FailClosed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("localhost:26379,serviceName=sql-master")]
    [InlineData("localhost:6379,localhost:6380")]
    public void AddHsSqlAgent_RejectsUnfencedRedisFailoverTopologies(string connectionString)
    {
        var options = CreateOptions();
        options.SqlConcurrencyProvider = "Redis";
        options.SqlConcurrencyConnectionString = connectionString;

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHsSqlAgent(options));
        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redis", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HsSqlAgentServiceOptions CreateOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        SecretProtection = Common.Services.TestSecrets.Create(),
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };
}
