using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace ToolBox.Middleware;

public class McpContextMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.HasJsonContentType())
        {
            context.Request.EnableBuffering();

            _ = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(context.Request.Body, options: null, context.RequestAborted);

            context.Request.Body.Seek(0, SeekOrigin.Begin);
        }

        await next.Invoke(context);
    }
}
