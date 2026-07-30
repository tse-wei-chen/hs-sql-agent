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

            if (!rateLimitService.TryAcquireKey(keyId, mode, permitLimit, windowSeconds, out var retryAfter))
            {
                await IpRateLimitMiddleware.WriteRateLimitedAsync(
                    context,
                    "MCP key request limit exceeded.",
                    retryAfter);
                return;
            }
        }

        await next(context);
    }
}
