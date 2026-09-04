using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Filters;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class ControllerSurfaceRegistrationTests
{
    [Fact]
    public void HostAuthorizationMode_DoesNotPublishBuiltInIdentityControllers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentHostAuthorization("Host.SqlAgentAdmin")
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        var controllers = GetHsSqlAgentControllers(provider);

        Assert.Contains(typeof(DbManagementController), controllers);
        Assert.DoesNotContain(typeof(AuthController), controllers);
        Assert.DoesNotContain(typeof(MemberController), controllers);
        Assert.DoesNotContain(typeof(RoleController), controllers);
    }

    [Fact]
    public void BuiltInAuthMode_PublishesIdentityControllers_EvenWhenSelectedAfterAdminApi()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentAdminApi();
        builder.AddHsSqlAgentBuiltInAuth();

        using var provider = services.BuildServiceProvider();
        var controllers = GetHsSqlAgentControllers(provider);

        Assert.Contains(typeof(AuthController), controllers);
        Assert.Contains(typeof(MemberController), controllers);
        Assert.Contains(typeof(RoleController), controllers);
    }

    [Fact]
    public void BuiltInAuthMode_AttachesIdentityStateGateOnlyToHsSqlAgentControllers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(HostControllerProbe).Assembly);
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentBuiltInAuth()
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        var descriptors = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToArray();

        var hs = descriptors.First(descriptor =>
            descriptor.ControllerTypeInfo.AsType() == typeof(DbManagementController));
        Assert.Contains(hs.FilterDescriptors, descriptor =>
            descriptor.Filter is ServiceFilterAttribute filter &&
            filter.ServiceType == typeof(HsSqlAgentBuiltInAuthStateFilter));

        var host = Assert.Single(descriptors, descriptor =>
            descriptor.ControllerTypeInfo.AsType() == typeof(HostControllerProbe));
        Assert.DoesNotContain(host.FilterDescriptors, descriptor =>
            descriptor.Filter is ServiceFilterAttribute filter &&
            filter.ServiceType == typeof(HsSqlAgentBuiltInAuthStateFilter));
    }

    [Fact]
    public void HostAuthorizationMode_DoesNotAttachBuiltInIdentityStateGate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentHostAuthorization("Host.SqlAgentAdmin")
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        var descriptors = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>();

        var hs = descriptors.First(descriptor =>
            descriptor.ControllerTypeInfo.AsType() == typeof(DbManagementController));
        Assert.DoesNotContain(hs.FilterDescriptors, descriptor =>
            descriptor.Filter is ServiceFilterAttribute filter &&
            filter.ServiceType == typeof(HsSqlAgentBuiltInAuthStateFilter));
    }

    private static Type[] GetHsSqlAgentControllers(IServiceProvider provider)
        => provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(descriptor => descriptor.ControllerTypeInfo.AsType())
            .Where(type => type.Assembly == typeof(RoleController).Assembly)
            .Distinct()
            .ToArray();

    private static HsSqlAgentServiceOptions CreateOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };
}

[ApiController]
public sealed class HostControllerProbe : ControllerBase
{
    [HttpGet("/api/host-controller-probe")]
    public IActionResult Get() => Ok();
}
