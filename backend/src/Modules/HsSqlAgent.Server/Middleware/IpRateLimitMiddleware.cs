using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Middleware;

public sealed class IpRateLimitMiddleware(
    ILayeredRateLimitService rateLimitService) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!rateLimitService.TryAcquireIp(ipAddress, out var retryAfter))
        {
            await WriteRateLimitedAsync(context, "IP request limit exceeded.", retryAfter);
            return;
        }

        await next(context);
    }

    internal static async Task WriteRateLimitedAsync(
        HttpContext context,
        string error,
        TimeSpan retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }
}
