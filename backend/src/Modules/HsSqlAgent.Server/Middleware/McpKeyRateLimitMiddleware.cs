using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Middleware;

public sealed class McpKeyRateLimitMiddleware(
    ILayeredRateLimitService rateLimitService) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Items.TryGetValue(McpContextItemKeys.AccessKeyId, out var keyIdValue) &&
            keyIdValue is int keyId)
        {
            var mode = context.Items.TryGetValue(McpContextItemKeys.RateLimitMode, out var modeValue) &&
                       modeValue is McpKeyRateLimitMode configuredMode
                ? configuredMode
                : McpKeyRateLimitMode.Inherit;
            int? permitLimit = context.Items.TryGetValue(McpContextItemKeys.PermitLimitOverride, out var permitValue) &&
                               permitValue is int configuredPermitLimit
                ? configuredPermitLimit
                : null;
            int? windowSeconds = context.Items.TryGetValue(McpContextItemKeys.WindowSecondsOverride, out var windowValue) &&
                                 windowValue is int configuredWindowSeconds
                ? configuredWindowSeconds
                : null;

            var result = await rateLimitService.AcquireKeyAsync(
                keyId,
                mode,
                permitLimit,
                windowSeconds,
                context.RequestAborted);
            if (!result.IsAllowed)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.ContentType = "application/json";
                context.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds)).ToString();
                await context.Response.WriteAsJsonAsync(
                    new { error = "MCP key request limit exceeded." },
                    context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
