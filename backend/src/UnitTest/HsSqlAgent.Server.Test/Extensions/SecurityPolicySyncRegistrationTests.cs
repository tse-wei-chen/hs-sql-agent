using Admin.Service.Interfaces;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class SecurityPolicySyncRegistrationTests
{
    [Fact]
    public void AddHsSqlAgent_ShouldRegisterMemoryPolicyPublisherByDefault()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgent(CreateOptions());

        var registration = Assert.Single(
            services,
            x => x.ServiceType == typeof(ISecurityPolicyChangePublisher));
        Assert.Equal(typeof(NoOpSecurityPolicyChangePublisher), registration.ImplementationType);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRegisterRedisPolicyBus()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();
        options.SecurityPolicySyncProvider = "Redis";
        options.SecurityPolicySyncConnectionString = "localhost:6379";

        services.AddHsSqlAgent(options);

        Assert.Contains(services, x =>
            x.ServiceType == typeof(RedisSecurityPolicyChangeBus));
        Assert.Single(services, x =>
            x.ServiceType == typeof(ISecurityPolicyChangePublisher));
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRejectRedisPolicyBusWithoutConnectionString()
    {
        var options = CreateOptions();
        options.SecurityPolicySyncProvider = "Redis";

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
