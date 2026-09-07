using System.Text;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Formatting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public sealed class HsSqlAgentJsonFormatterSelectionTests
{
    [Fact]
    public void InputFormatter_SelectsHsControllerButNotHostController()
    {
        var formatter = new HsSqlAgentJsonInputFormatter();

        Assert.True(formatter.CanRead(CreateInputContext(typeof(DbManagementController))));
        Assert.False(formatter.CanRead(CreateInputContext(typeof(HostIsolationProbeController))));
    }

    [Fact]
    public void OutputFormatter_SelectsHsControllerButNotHostController()
    {
        var formatter = new HsSqlAgentJsonOutputFormatter();

        Assert.True(formatter.CanWriteResult(CreateOutputContext(typeof(DbManagementController))));
        Assert.False(formatter.CanWriteResult(CreateOutputContext(typeof(HostIsolationProbeController))));
    }

    private static InputFormatterContext CreateInputContext(Type controllerType)
    {
        var httpContext = CreateHttpContext(controllerType);
        httpContext.Request.ContentType = "application/json";

        return new InputFormatterContext(
            httpContext,
            "request",
            new ModelStateDictionary(),
            new EmptyModelMetadataProvider().GetMetadataForType(typeof(ProbeRequest)),
            (stream, encoding) => new StreamReader(stream, encoding));
    }

    private static OutputFormatterCanWriteContext CreateOutputContext(Type controllerType)
    {
        var httpContext = CreateHttpContext(controllerType);
        return new OutputFormatterWriteContext(
            httpContext,
            (stream, encoding) => new StreamWriter(stream, encoding),
            typeof(ProbeRequest),
            new ProbeRequest())
        {
            ContentType = "application/json"
        };
    }

    private static DefaultHttpContext CreateHttpContext(Type controllerType)
    {
        var context = new DefaultHttpContext();
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = controllerType.GetTypeInfo()
        };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(descriptor),
            controllerType.Name));
        return context;
    }

    private sealed class ProbeRequest
    {
        public string Name { get; init; } = string.Empty;
    }
}
