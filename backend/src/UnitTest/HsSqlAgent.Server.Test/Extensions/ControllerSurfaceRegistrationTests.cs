using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class ControllerSurfaceRegistrationTests
{
    [Fact]
    public void HostAuthorizationMode_DoesNotPublishBuiltInAuthController()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentHostAuthorization("Host.SqlAgentAdmin")
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        var controllers = GetHsSqlAgentControllers(provider);

        Assert.Contains(typeof(RoleController), controllers);
        Assert.DoesNotContain(typeof(AuthController), controllers);
    }

    [Fact]
    public void BuiltInAuthMode_PublishesAuthController_EvenWhenSelectedAfterAdminApi()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentAdminApi();
        builder.AddHsSqlAgentBuiltInAuth();

        using var provider = services.BuildServiceProvider();
        var controllers = GetHsSqlAgentControllers(provider);

        Assert.Contains(typeof(RoleController), controllers);
        Assert.Contains(typeof(AuthController), controllers);
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
