using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ToolBox.Middleware;

public class McpResponseFlattenerMiddleware(ILogger<McpResponseFlattenerMiddleware> logger) : IMiddleware
{
    private readonly ILogger<McpResponseFlattenerMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.Value?.Contains("/mcp") ?? true)
        {
            await next(context);
            return;
        }

        var originalBodyStream = context.Response.Body;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        try
        {
            await next(context);

            context.Response.Body = originalBodyStream;
            responseBody.Seek(0, SeekOrigin.Begin);

            if (context.Response.ContentType != null && context.Response.ContentType.Contains("application/json"))
            {
                var responseText = await new StreamReader(responseBody).ReadToEndAsync();
                var modifiedText = ProcessMcpResponse(responseText);
                await context.Response.WriteAsync(modifiedText);
            }
            else
            {
                await responseBody.CopyToAsync(originalBodyStream);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in McpResponseFlattenerMiddleware");
            context.Response.Body = originalBodyStream;
            responseBody.Seek(0, SeekOrigin.Begin);
            await responseBody.CopyToAsync(originalBodyStream);
        }
    }

    private static string ProcessMcpResponse(string json)
    {
        return McpResponseUtils.ProcessMcpResponse(json);
    }
}
