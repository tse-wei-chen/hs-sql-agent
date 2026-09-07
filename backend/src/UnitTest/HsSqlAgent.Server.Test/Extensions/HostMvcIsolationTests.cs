using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Models;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Filters;
using HsSqlAgent.Server.Formatting;
using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Models;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
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
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IProblemDetailsService));
    }

    [Fact]
    public void AddHsSqlAgentStandalonePreset_OwnsExceptionFallback()
    {
        var services = new ServiceCollection();
        services.AddHsSqlAgent(CreateStandaloneOptions());

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IExceptionHandler) &&
            descriptor.ImplementationType == typeof(GlobalExceptionHandler));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProblemDetailsService));
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
    public void AddHsSqlAgentAdminApi_InstallsStableHsJsonWireContract()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddJsonOptions(json =>
        {
            json.JsonSerializerOptions.PropertyNamingPolicy = null;
            json.JsonSerializerOptions.DictionaryKeyPolicy = null;
            json.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
        });
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        var mvc = provider.GetRequiredService<IOptions<MvcOptions>>().Value;
        var input = Assert.IsType<HsSqlAgentJsonInputFormatter>(mvc.InputFormatters[0]);
        var output = Assert.IsType<HsSqlAgentJsonOutputFormatter>(mvc.OutputFormatters[0]);

        Assert.Same(JsonNamingPolicy.CamelCase, input.SerializerOptions.PropertyNamingPolicy);
        Assert.Same(JsonNamingPolicy.CamelCase, input.SerializerOptions.DictionaryKeyPolicy);
        Assert.True(input.SerializerOptions.PropertyNameCaseInsensitive);
        Assert.Contains(input.SerializerOptions.Converters, converter => converter is JsonStringEnumConverter);

        Assert.Same(JsonNamingPolicy.CamelCase, output.SerializerOptions.PropertyNamingPolicy);
        Assert.Same(JsonNamingPolicy.CamelCase, output.SerializerOptions.DictionaryKeyPolicy);
        Assert.True(output.SerializerOptions.PropertyNameCaseInsensitive);
        Assert.Contains(output.SerializerOptions.Converters, converter => converter is JsonStringEnumConverter);

        var json = JsonSerializer.Serialize(
            new HsJsonContractProbe { DbManagementId = 7, Provider = SqlAgentToolType.Postgres },
            output.SerializerOptions);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("dbManagementId", out var dbId));
        Assert.Equal(7, dbId.GetInt32());
        Assert.Equal("Postgres", document.RootElement.GetProperty("provider").GetString());
        Assert.False(document.RootElement.TryGetProperty("DbManagementId", out _));

        var roundTrip = JsonSerializer.Deserialize<HsJsonContractProbe>(
            "{\"DBMANAGEMENTID\":7,\"PROVIDER\":\"postgres\"}",
            input.SerializerOptions);
        Assert.NotNull(roundTrip);
        Assert.Equal(7, roundTrip.DbManagementId);
        Assert.Equal(SqlAgentToolType.Postgres, roundTrip.Provider);
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

    private static HsSqlAgentServiceOptions CreateStandaloneOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };

    private sealed class HsJsonContractProbe
    {
        public int DbManagementId { get; init; }
        public SqlAgentToolType Provider { get; init; }
    }
}

[ApiController]
public sealed class HostIsolationProbeController : ControllerBase
{
    [HttpGet("/host/isolation-probe")]
    public IActionResult Get() => Ok();
}
