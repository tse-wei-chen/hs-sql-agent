using Auth.Service.Data;
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
    public void AddHsSqlAgentAdminStore_DoesNotInstallBuiltInIdentityPersistence()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentAdminStore();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Admin.Service.Data.AdminContext));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(AuthContext));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthContext));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IMemberService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IRoleService));
    }

    [Fact]
    public void AddHsSqlAgentMcp_DoesNotImplicitlyInstallBuiltInUserAuthentication()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();

        services.AddHsSqlAgentCore(options)
            .AddHsSqlAgentMcp();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(AuthContext));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthContext));
    }

    [Fact]
    public void AddHsSqlAgentHostAuthorization_DoesNotInstallBuiltInIdentityPersistence()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentHostAuthorization("Host.SqlAgentAdmin")
            .AddHsSqlAgentAdminApi();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(AuthContext));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthContext));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IMemberService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IRoleService));
    }

    [Fact]
    public void AddHsSqlAgentBuiltInAuth_InstallsIdentityPersistence()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentBuiltInAuth();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AuthContext));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAuthContext));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IMemberService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRoleService));
    }

    [Fact]
    public void AddHsSqlAgent_RemainsTheFullCompatibilityPreset()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();

        services.AddHsSqlAgent(options);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AuthContext));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Admin.Service.Data.AdminContext));
    }

    private static HsSqlAgentServiceOptions CreateOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };
}
