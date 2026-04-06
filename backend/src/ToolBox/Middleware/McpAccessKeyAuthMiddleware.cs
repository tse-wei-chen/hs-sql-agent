using Microsoft.AspNetCore.Http;
using Modules.Interfaces;
using ToolBox.Background;
using ToolBox.Models;

namespace ToolBox.Middleware;

public class McpAccessKeyAuthMiddleware(
    IMcpAccessKeyService keyService,
    IAuditService auditService,
    IMcpAccessKeyLastUsedQueue lastUsedQueue,
    ILogger<McpAccessKeyAuthMiddleware> logger) : IMiddleware
{
    private const int MaxAuthHeaderLength = 4096;
    private const int MaxMcpKeyHeaderLength = 1024;
    private const int MaxCorsRequestHeadersLength = 2048;
    private static readonly HashSet<string> AllowedCorsMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get,
        HttpMethods.Post
    };

    private readonly IMcpAccessKeyService _keyService = keyService;
    private readonly IAuditService _auditService = auditService;
    private readonly IMcpAccessKeyLastUsedQueue _lastUsedQueue = lastUsedQueue;
    private readonly ILogger<McpAccessKeyAuthMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        if (TryHandleCorsPreflight(context))
        {
            return;
        }

        if (!TryExtractKey(context.Request, out var rawKey, out var keyError))
        {
            await WriteUnauthorizedAsync(context, keyError ?? "Missing MCP access key.");
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
        context.Items[McpContextItemKeys.CorsAllowedOrigins] = validation.CorsAllowedOrigins ?? string.Empty;
        context.Items[McpContextItemKeys.SqlProvider] = validation.SqlProvider ?? string.Empty;
        context.Items[McpContextItemKeys.SqlConnectionString] = validation.SqlConnectionString ?? string.Empty;

        if (!TryApplyCorsPolicy(context, validation.CorsAllowedOriginsSet, out var corsError))
        {
            await WriteForbiddenAsync(context, corsError ?? "Origin not allowed.");
            await AuditFailedAsync(context, "cors_origin_not_allowed", context.RequestAborted);
            return;
        }

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

        if (!_lastUsedQueue.TryEnqueue(validation.KeyId.Value))
        {
            _logger.LogWarning("Skipping MCP key last-used update because queue is full. keyId={KeyId}", validation.KeyId.Value);
        }

        await next(context);
    }

    private static bool TryApplyCorsPolicy(HttpContext context, IReadOnlySet<string>? corsAllowedOriginsSet, out string? error)
    {
        error = null;

        var originHeader = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(originHeader))
        {
            return true;
        }

        if (!TryNormalizeOrigin(originHeader, out var requestOrigin))
        {
            error = "Invalid Origin header.";
            return false;
        }

        if (corsAllowedOriginsSet is null || corsAllowedOriginsSet.Count == 0)
        {
            error = "Origin is not allowed for this API key.";
            return false;
        }

        if (!corsAllowedOriginsSet.Contains(requestOrigin))
        {
            error = "Origin is not allowed for this API key.";
            return false;
        }

        context.Response.Headers.AccessControlAllowOrigin = requestOrigin;
        AppendVaryHeader(context.Response.Headers, "Origin");

        return true;
    }

    private static bool TryHandleCorsPreflight(HttpContext context)
    {
        if (!HttpMethods.IsOptions(context.Request.Method))
        {
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var requestedMethod = context.Request.Headers.AccessControlRequestMethod.ToString();
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(requestedMethod))
        {
            return false;
        }

        if (!TryNormalizeOrigin(origin, out var normalizedOrigin))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return true;
        }

        if (!AllowedCorsMethods.Contains(requestedMethod))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        // Browsers do not include API key values on preflight, so key-specific checks happen on the actual request.
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers.AccessControlAllowOrigin = normalizedOrigin;
        context.Response.Headers.AccessControlAllowMethods = requestedMethod;

        var requestedHeaders = context.Request.Headers.AccessControlRequestHeaders.ToString();
        if (requestedHeaders.Length > MaxCorsRequestHeadersLength)
        {
            context.Response.StatusCode = StatusCodes.Status431RequestHeaderFieldsTooLarge;
            return true;
        }

        context.Response.Headers.AccessControlAllowHeaders = string.IsNullOrWhiteSpace(requestedHeaders)
            ? "authorization,content-type,x-mcp-server-key"
            : requestedHeaders;

        context.Response.Headers.AccessControlMaxAge = "600";
        AppendVaryHeader(context.Response.Headers, "Origin");
        AppendVaryHeader(context.Response.Headers, "Access-Control-Request-Method");
        AppendVaryHeader(context.Response.Headers, "Access-Control-Request-Headers");
        return true;
    }

    private static void AppendVaryHeader(IHeaderDictionary headers, string value)
    {
        if (!headers.TryGetValue("Vary", out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            headers.Vary = value;
            return;
        }

        var values = existing
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (values.Add(value))
        {
            headers.Vary = string.Join(", ", values);
        }
    }

    private static bool TryNormalizeOrigin(string origin, out string normalized)
    {
        normalized = string.Empty;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Scheme)
            || string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        normalized = parsed
            .GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped)
            .ToLowerInvariant();
        return true;
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

    private static bool TryExtractKey(HttpRequest request, out string rawKey, out string? error)
    {
        rawKey = string.Empty;
        error = null;

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.Length > MaxAuthHeaderLength)
        {
            error = "Authorization header is too large.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(authorization) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            rawKey = authorization[7..].Trim();
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                error = "Missing MCP access key.";
                return false;
            }

            if (rawKey.Length > MaxMcpKeyHeaderLength)
            {
                error = "MCP access key is too large.";
                return false;
            }

            return true;
        }

        var headerKey = request.Headers["X-MCP-Server-Key"].ToString();
        if (headerKey.Length > MaxMcpKeyHeaderLength)
        {
            error = "MCP access key is too large.";
            return false;
        }

        rawKey = headerKey.Trim();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            error = "Missing MCP access key.";
            return false;
        }

        return true;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
