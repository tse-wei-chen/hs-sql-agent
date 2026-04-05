using Microsoft.AspNetCore.Http;
using Modules.Interfaces;
using ToolBox.Models;

namespace ToolBox.Middleware;

public class McpAccessKeyAuthMiddleware(IMcpAccessKeyService keyService, IAuditService auditService) : IMiddleware
{
    private readonly IMcpAccessKeyService _keyService = keyService;
    private readonly IAuditService _auditService = auditService;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        var rawKey = ExtractKey(context.Request);
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            await WriteUnauthorizedAsync(context, "Missing MCP access key.");
            await AuditFailedAsync(context, "missing_key", context.RequestAborted);
            return;
        }

        var validation = await _keyService.ValidateAsync(rawKey, context.RequestAborted);
        if (!validation.IsValid || validation.KeyId is null)
        {
            await WriteUnauthorizedAsync(context, "Invalid MCP access key.");
            await AuditFailedAsync(context, validation.Reason ?? "invalid_key", context.RequestAborted);
            return;
        }

        context.Items[McpContextItemKeys.AccessKeyId] = validation.KeyId.Value;
        context.Items[McpContextItemKeys.AccessKeyName] = validation.Name ?? string.Empty;
        context.Items[McpContextItemKeys.AllowedTools] = validation.AllowedTools ?? string.Empty;
        context.Items[McpContextItemKeys.SqlProvider] = validation.SqlProvider ?? string.Empty;
        context.Items[McpContextItemKeys.SqlConnectionString] = validation.SqlConnectionString ?? string.Empty;

        if (validation.PermitLimitOverride.HasValue)
        {
            context.Items[McpContextItemKeys.PermitLimit] = validation.PermitLimitOverride.Value;
        }

        if (validation.WindowSecondsOverride.HasValue)
        {
            context.Items[McpContextItemKeys.WindowSeconds] = validation.WindowSecondsOverride.Value;
        }

        if (validation.QueueLimitOverride.HasValue)
        {
            context.Items[McpContextItemKeys.QueueLimit] = validation.QueueLimitOverride.Value;
        }

        _ = _keyService.TouchLastUsedAsync(validation.KeyId.Value, CancellationToken.None);
        await next(context);
    }

    private async Task AuditFailedAsync(HttpContext context, string reason, CancellationToken cancellationToken)
    {
        await _auditService.WriteAsync(
            action: "mcp.key.auth.failed",
            target: "/mcp",
            result: "failed",
            detail: reason,
            actorType: "mcp-key",
            actorId: null,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            userAgent: context.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization[7..].Trim();
        }

        var headerKey = request.Headers["X-MCP-Server-Key"].ToString();
        return string.IsNullOrWhiteSpace(headerKey) ? null : headerKey.Trim();
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"error\":\"{message}\"}}");
    }
}
