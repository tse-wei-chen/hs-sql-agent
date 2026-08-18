using Admin.Service.Interfaces;
using Admin.Service.Services;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class OutboundDeliverySyncRegistrationTests
{
    [Fact]
    public void AddHsSqlAgent_ShouldRegisterMemoryOutboundDeliverySignalByDefault()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgent(CreateOptions());

        var registration = Assert.Single(
            services,
            x => x.ServiceType == typeof(IOutboundDeliverySignal));
        Assert.Equal(typeof(OutboundDeliverySignal), registration.ImplementationType);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRegisterRedisOutboundDeliverySignal()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();
        options.OutboundDeliverySyncProvider = "Redis";
        options.OutboundDeliverySyncConnectionString = "localhost:6379";

        services.AddHsSqlAgent(options);

        Assert.Contains(services, x =>
            x.ServiceType == typeof(RedisOutboundDeliverySignal));
        Assert.Single(services, x =>
            x.ServiceType == typeof(IOutboundDeliverySignal));
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRejectRedisOutboundDeliverySignalWithoutConnectionString()
    {
        var options = CreateOptions();
        options.OutboundDeliverySyncProvider = "Redis";

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
