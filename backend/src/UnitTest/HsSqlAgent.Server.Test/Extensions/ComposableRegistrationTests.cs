using Auth.Service.Interfaces;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class ComposableRegistrationTests
{
    [Fact]
    public void AddHsSqlAgentCore_DoesNotInstallOptionalModules()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgentCore(new HsSqlAgentServiceOptions());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider));
    }

    [Fact]
    public void AddHsSqlAgentRuntime_DoesNotRequireAdminStoreOrSecrets()
    {
        var services = new ServiceCollection();
        var builder = services.AddHsSqlAgentCore(new HsSqlAgentServiceOptions());

        builder.AddHsSqlAgentRuntime();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(Admin.Service.Data.AdminContext));
    }

    [Fact]
    public void AddHsSqlAgentMcp_DoesNotImplicitlyInstallBuiltInUserAuthentication()
    {
        var services = new ServiceCollection();
        var options = new HsSqlAgentServiceOptions
        {
            AdminConnectionString = "Data Source=:memory:",
            HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes"
        };

        services.AddHsSqlAgentCore(options)
            .AddHsSqlAgentMcp();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
    }

    [Fact]
    public void AddHsSqlAgent_RemainsTheFullCompatibilityPreset()
    {
        var services = new ServiceCollection();
        var options = new HsSqlAgentServiceOptions
        {
            AdminConnectionString = "Data Source=:memory:",
            HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
            JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
        };

        services.AddHsSqlAgent(options);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Admin.Service.Data.AdminContext));
    }
}
