using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Models;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Filters;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class HostMvcIsolationTests
{
    [Fact]
    public void AddHsSqlAgentAdminApi_AttachesHsFiltersOnlyToHsControllers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(HostIsolationProbeController).Assembly);
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentHostAuthorization("Host.SqlAgentAdmin")
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        var descriptors = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToArray();

        var host = Assert.Single(descriptors, descriptor =>
            descriptor.ControllerTypeInfo.AsType() == typeof(HostIsolationProbeController));
        Assert.DoesNotContain(host.FilterDescriptors, descriptor => IsHsScopedFilter(descriptor.Filter));

        var hs = descriptors.First(descriptor =>
            descriptor.ControllerTypeInfo.AsType() == typeof(DbManagementController));
        Assert.Contains(hs.FilterDescriptors, descriptor =>
            descriptor.Filter is ServiceFilterAttribute filter &&
            filter.ServiceType == typeof(HsSqlAgentValidationFilter));
        Assert.Contains(hs.FilterDescriptors, descriptor =>
            descriptor.Filter is ServiceFilterAttribute filter &&
            filter.ServiceType == typeof(HsSqlAgentExceptionFilter));
    }

    [Fact]
    public void AddHsSqlAgentAdminApi_DoesNotInstallGlobalExceptionHandler()
    {
        var services = new ServiceCollection();
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentAdminApi();

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IExceptionHandler) &&
            descriptor.ImplementationType?.Namespace == "HsSqlAgent.Server.Middleware");
    }

    [Fact]
    public void AddHsSqlAgentAdminApi_PreservesHostJsonOptions()
    {
        var services = new ServiceCollection();
        services.AddControllers().AddJsonOptions(json =>
        {
            json.JsonSerializerOptions.PropertyNamingPolicy = null;
            json.JsonSerializerOptions.DictionaryKeyPolicy = null;
            json.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
        });
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        var json = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        Assert.Null(json.PropertyNamingPolicy);
        Assert.Null(json.DictionaryKeyPolicy);
        Assert.False(json.PropertyNameCaseInsensitive);
        Assert.DoesNotContain(json.Converters, converter => converter is JsonStringEnumConverter);
    }

    [Fact]
    public void McpRateLimitMode_PreservesStringWireContractWithoutGlobalJsonConverter()
    {
        Assert.Equal("\"Custom\"", JsonSerializer.Serialize(McpKeyRateLimitMode.Custom));
    }

    private static bool IsHsScopedFilter(object filter)
        => filter is ServiceFilterAttribute serviceFilter &&
           (serviceFilter.ServiceType == typeof(HsSqlAgentValidationFilter) ||
            serviceFilter.ServiceType == typeof(HsSqlAgentExceptionFilter));

    private static HsSqlAgentServiceOptions CreateOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes"
    };
}

[ApiController]
public sealed class HostIsolationProbeController : ControllerBase
{
    [HttpGet("/host/isolation-probe")]
    public IActionResult Get() => Ok();
}
