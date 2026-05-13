using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using ToolBox.Background;

namespace ToolBox.Middleware;

public class McpAccessKeyAuthMiddleware(
    IMcpAccessKeyService keyService,
    IAuditService auditService,
    IMcpAccessKeyLastUsedQueue lastUsedQueue,
    IMemoryCache cache,
    IConfiguration configuration,
    IDbManagementService dbManagementService,
    IDbSetterService dbSetterService,
    ICryptoService cryptoService,
    IOptions<McpKeySettings> mcpKeySettings,
    ILogger<McpAccessKeyAuthMiddleware> logger) : IMiddleware
{
    private const int StripedLockCount = 64;
    private static readonly SemaphoreSlim[] StripedLocks = [.. Enumerable.Range(0, StripedLockCount).Select(_ => new SemaphoreSlim(1, 1))];
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NegativeCacheExpiry = TimeSpan.FromMinutes(1);
    private static readonly byte[] UnauthorizedResponseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = "Invalid MCP access key." }));
    private static readonly byte[] MissingKeyResponseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = "Missing MCP access key." }));
    private static readonly byte[] ForbiddenResponseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = "Origin not allowed." }));
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
    private readonly IMemoryCache _cache = cache;
    private readonly IConfiguration _configuration = configuration;
    private readonly IDbManagementService _dbManagementService = dbManagementService;
    private readonly IDbSetterService _dbSetterService = dbSetterService;
    private readonly ICryptoService _cryptoService = cryptoService;
    private readonly byte[] _hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
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
            await WriteUnauthorizedAsync(context, keyError == "Missing MCP access key." ? MissingKeyResponseBytes : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = keyError })));
            await AuditFailedAsync(context, "missing_key", context.RequestAborted);
            return;
        }

        var validation = await GetOrValidateKeyAsync(rawKey, context.RequestAborted);
        if (!validation.IsValid || validation.KeyId is null)
        {
            await WriteUnauthorizedAsync(context, UnauthorizedResponseBytes);
            await AuditFailedAsync(context, validation.Reason ?? "invalid_key", context.RequestAborted);
            return;
        }
        var provider = validation.SqlProvider ?? string.Empty;
        var connString = string.Empty;

        if (validation.DbManagementId.HasValue)
        {
            var dbc = await _dbManagementService.GetDbByIdAsync(validation.DbManagementId.Value, true, context.RequestAborted);
            if (dbc is DbManagementPwdVM pwdDbc)
            {
                provider = pwdDbc.SqlProvider ?? string.Empty;
                connString = await _dbSetterService.BuildDbConnectionAsync(new BuildDbConnectionModel
                {
                    Provider = pwdDbc.SqlProvider ?? "MsSqlServer",
                    Host = pwdDbc.Host,
                    Port = pwdDbc.Port,
                    Database = pwdDbc.Database,
                    Username = pwdDbc.Username,
                    Password = _cryptoService.DecryptText(pwdDbc.PasswordHash, _hmacSecret),
                    ExtraSettings = pwdDbc.ExtraSettings
                }, context.RequestAborted) ?? string.Empty;
            }
        }
        else if (string.Equals(provider, "Global", StringComparison.OrdinalIgnoreCase))
        {
            var globalConfig = _configuration.GetSection("SqlConfig");
            provider = globalConfig["Provider"] ?? "MsSqlServer";
            connString = globalConfig["ConnectionString"] ?? string.Empty;
        }

        context.Items[McpContextItemKeys.AccessKeyId] = validation.KeyId.Value;
        context.Items[McpContextItemKeys.AccessKeyName] = validation.Name ?? string.Empty;
        context.Items[McpContextItemKeys.AllowedTools] = validation.AllowedTools ?? string.Empty;
        context.Items[McpContextItemKeys.CorsAllowedOrigins] = validation.CorsAllowedOrigins ?? string.Empty;
        context.Items[McpContextItemKeys.SqlProvider] = provider;
        context.Items[McpContextItemKeys.SqlConnectionString] = connString;
        if (!TryApplyCorsPolicy(context, validation.CorsAllowedOriginsSet, out _))
        {
            await WriteForbiddenAsync(context, ForbiddenResponseBytes);
            await AuditFailedAsync(context, "cors_origin_not_allowed", context.RequestAborted);
            return;
        }

        if (!_lastUsedQueue.TryEnqueue(validation.KeyId.Value))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Skipping MCP key last-used update because queue is full. keyId={KeyId}", validation.KeyId.Value);
            }
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

    private static async Task WriteUnauthorizedAsync(HttpContext context, byte[] responseBytes)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(responseBytes);
    }

    private static async Task WriteForbiddenAsync(HttpContext context, byte[] responseBytes)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(responseBytes);
    }

    private async Task<McpAccessKeyValidationResult> GetOrValidateKeyAsync(string rawKey, CancellationToken ct)
    {
        var keyHash = ComputeKeyHash(rawKey);
        var cacheKey = $"mcp_auth_v2_{keyHash}";

        if (_cache.TryGetValue(cacheKey, out McpAccessKeyValidationResult? cachedResult) && cachedResult is not null)
        {
            return cachedResult;
        }

        var lockIndex = Math.Abs(cacheKey.GetHashCode()) % StripedLocks.Length;
        var semaphore = StripedLocks[lockIndex];

        await semaphore.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cachedResult) && cachedResult is not null)
            {
                return cachedResult;
            }

            var result = await _keyService.ValidateAsync(rawKey, ct);

            var expiry = result.IsValid ? CacheExpiry : NegativeCacheExpiry;
            _cache.Set(cacheKey, result, expiry);

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }


    private static string ComputeKeyHash(string rawKey)
    {
        var inputBytes = Encoding.UTF8.GetBytes(rawKey);
        var hashBytes = SHA256.HashData(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}


