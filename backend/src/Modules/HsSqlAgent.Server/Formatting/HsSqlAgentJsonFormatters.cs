using System.Text.Json;
using System.Text.Json.Serialization;
using HsSqlAgent.Server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Logging.Abstractions;

namespace HsSqlAgent.Server.Formatting;

internal static class HsSqlAgentJsonContract
{
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static JsonOptions CreateMvcJsonOptions()
    {
        var mvcOptions = new JsonOptions();
        var options = mvcOptions.JsonSerializerOptions;
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new JsonStringEnumConverter());
        return mvcOptions;
    }

    public static bool IsHsSqlAgentController(HttpContext httpContext)
    {
        var action = httpContext.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return action?.ControllerTypeInfo.Assembly == typeof(AuthController).Assembly;
    }
}

internal sealed class HsSqlAgentJsonInputFormatter : SystemTextJsonInputFormatter
{
    public HsSqlAgentJsonInputFormatter()
        : base(
            HsSqlAgentJsonContract.CreateMvcJsonOptions(),
            NullLogger<SystemTextJsonInputFormatter>.Instance)
    {
    }

    public override bool CanRead(InputFormatterContext context)
        => HsSqlAgentJsonContract.IsHsSqlAgentController(context.HttpContext)
           && base.CanRead(context);
}

internal sealed class HsSqlAgentJsonOutputFormatter : SystemTextJsonOutputFormatter
{
    public HsSqlAgentJsonOutputFormatter()
        : base(HsSqlAgentJsonContract.CreateSerializerOptions())
    {
    }

    public override bool CanWriteResult(OutputFormatterCanWriteContext context)
        => HsSqlAgentJsonContract.IsHsSqlAgentController(context.HttpContext)
           && base.CanWriteResult(context);
}
