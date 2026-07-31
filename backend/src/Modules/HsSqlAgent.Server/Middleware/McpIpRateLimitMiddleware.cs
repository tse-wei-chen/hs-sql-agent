using Admin.Service.Interfaces;
using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Middleware;

public sealed class McpIpRateLimitMiddleware(
    IRateLimitingRuntimeState runtimeState,
    IRequestRateLimiter rateLimiter) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var settings = runtimeState.GetCurrent();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await rateLimiter.AcquireAsync(
            new RateLimitRequest(
                $"ip:{ip}",
                settings.PermitLimit,
                TimeSpan.FromSeconds(settings.WindowSeconds)),
            context.RequestAborted);

        if (!result.IsAllowed)
        {
            await WriteRejectedResponseAsync(context, result.RetryAfter);
            return;
        }

        await next(context);
    }

    private static async Task WriteRejectedResponseAsync(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await context.Response.WriteAsJsonAsync(
            new { error = "IP request limit exceeded." },
            context.RequestAborted);
    }
}
