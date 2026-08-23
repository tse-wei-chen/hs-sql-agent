using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class DmlApprovalChallengeStoreRegistrationTests
{
    [Fact]
    public void AddHsSqlAgent_ShouldRegisterMemoryApprovalStoreByDefault()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgent(CreateOptions());

        var store = Assert.Single(services, x => x.ServiceType == typeof(IDmlApprovalChallengeStore));
        Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
        Assert.Equal(typeof(InMemoryDmlApprovalChallengeStore), store.ImplementationType);

        var runtime = Assert.Single(services, x => x.ServiceType == typeof(TypedDmlRuntime));
        Assert.Equal(ServiceLifetime.Singleton, runtime.Lifetime);
        Assert.NotNull(runtime.ImplementationFactory);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRegisterRedisApprovalStore()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();
        options.DmlApprovalStoreProvider = "Redis";
        options.DmlApprovalStoreConnectionString = "localhost:6379";
        options.DmlApprovalStoreKeyPrefix = "test:dml:";

        services.AddHsSqlAgent(options);

        var store = Assert.Single(services, x => x.ServiceType == typeof(IDmlApprovalChallengeStore));
        Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
        Assert.Equal(typeof(RedisDmlApprovalChallengeStore), store.ImplementationType);
        var redisOptions = Assert.Single(
            services,
            x => x.ServiceType == typeof(RedisDmlApprovalChallengeOptions));
        var value = Assert.IsType<RedisDmlApprovalChallengeOptions>(redisOptions.ImplementationInstance);
        Assert.Equal("localhost:6379", value.ConnectionString);
        Assert.Equal("test:dml:", value.KeyPrefix);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRejectRedisApprovalStoreWithoutConnectionString()
    {
        var options = CreateOptions();
        options.DmlApprovalStoreProvider = "Redis";

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHsSqlAgent(options));

        Assert.Contains("connection string is required", exception.Message);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRejectUnknownApprovalStoreProvider()
    {
        var options = CreateOptions();
        options.DmlApprovalStoreProvider = "Unknown";

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHsSqlAgent(options));

        Assert.Contains("Unsupported DML approval store provider", exception.Message);
    }

    private static HsSqlAgentServiceOptions CreateOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };
}
