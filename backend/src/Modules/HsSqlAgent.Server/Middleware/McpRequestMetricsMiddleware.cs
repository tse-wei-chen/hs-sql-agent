using System.Diagnostics;
using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Middleware;

public sealed class McpRequestMetricsMiddleware(IHsSqlAgentMetrics metrics) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var stopwatch = Stopwatch.StartNew();
        metrics.McpRequestStarted();
        try
        {
            await next(context);
        }
        finally
        {
            metrics.McpRequestCompleted(context.Response.StatusCode, stopwatch.Elapsed);
        }
    }
}
