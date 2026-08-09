using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Admin.Service.Services;

public class McpAccessKeyService(
    IAdminContext context,
    IOptions<McpKeySettings> mcpKeySettings,
    ICryptoService cryptoService,
    ICacheService cache,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState) : IMcpAccessKeyService
{
    private const int KeyPrefixLength = 8;
    private static readonly TimeSpan RevocationTombstoneExpiry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChangedKeyRefreshExpiry = TimeSpan.FromMinutes(6);
    private static readonly char[] CorsOriginsSeparators = [',', ';', '\n', '\r'];
    private readonly IAdminContext _context = context;
    private readonly byte[] _hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
    private readonly ICryptoService _cryptoService = cryptoService;
    private readonly ICacheService _cache = cache;
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;

    public async Task<McpAccessKeyIssueResult> IssueKeyAsync(
        IssueMcpAccessKeyModel request,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Key name is required.", nameof(request.Name));
        }
        NormalizeAndValidateRateLimit(request);

        var plaintext = GenerateRawKey();
        var entity = CreateEntity(request, plaintext, actorId);

        _context.McpAccessKeys.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return CreateIssueResult(entity, plaintext);
    }

    public async Task<IReadOnlyCollection<McpAccessKeyListItem>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var items = await _context.McpAccessKeys
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new McpAccessKeyListItem
            {
                Id = x.Id,
                Name = x.Name,
                KeyPrefix = x.KeyPrefix,
                IsActive = x.IsActive && !x.RevokedAt.HasValue &&
                    (!x.ExpiresAt.HasValue || x.ExpiresAt > now),
                IsExpired = x.ExpiresAt.HasValue && x.ExpiresAt <= now,
                IsExpiringSoon = x.IsActive && !x.RevokedAt.HasValue &&
                    x.ExpiresAt.HasValue && x.ExpiresAt > now &&
                    x.ExpiresAt <= now.AddDays(7),
                ExpiresAt = x.ExpiresAt,
                LastUsedAt = x.LastUsedAt,
                AllowedTools = x.AllowedTools,
                CorsAllowedOrigins = x.CorsAllowedOrigins,
                SqlProvider = x.SqlProvider,
                DbManagementId = x.DbManagementId,
                TableWhitelist = x.TableWhitelist,
                RateLimitMode = x.RateLimitMode,
                PermitLimitOverride = x.PermitLimitOverride,
                WindowSecondsOverride = x.WindowSecondsOverride,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var dbIds = items
            .Where(x => x.DbManagementId.HasValue)
            .Select(x => x.DbManagementId!.Value)
            .Distinct()
            .ToArray();

        if (dbIds.Length > 0)
        {
            var databases = await _context.DbManagement
                .AsNoTracking()
                .Where(x => dbIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Name, x.SqlProvider })
                .ToDictionaryAsync(x => x.Id, cancellationToken);

            foreach (var item in items)
            {
                if (item.DbManagementId.HasValue &&
                    databases.TryGetValue(item.DbManagementId.Value, out var database))
                {
                    item.DbManagementName = database.Name;
                    item.SqlProvider = database.SqlProvider;
                }
            }
        }

        var policy = _securityPolicyRuntimeState.GetCurrent();
        foreach (var item in items)
            ApplyEffectiveRateLimit(item, policy.KeyPermitLimit, policy.KeyWindowSeconds);

        return items;
    }

    public async Task<McpAccessKeyListItem?> UpdateKeyAsync(
        int id,
        UpdateMcpAccessKeyRequest request,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Key name is required.", nameof(request.Name));
        NormalizeAndValidateRateLimit(request);

        var entity = await _context.McpAccessKeys.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return null;

        entity.Name = request.Name.Trim();
        entity.ExpiresAt = request.ExpiresAt;
        entity.AllowedTools = NormalizeNullable(request.AllowedTools);
        entity.CorsAllowedOrigins = NormalizeCorsAllowedOrigins(request.CorsAllowedOrigins);
        entity.DbManagementId = request.DbManagementId;
        entity.TableWhitelist = NormalizeNullable(request.TableWhitelist);
        entity.RateLimitMode = request.RateLimitMode;
        entity.PermitLimitOverride = request.PermitLimitOverride;
        entity.WindowSecondsOverride = request.WindowSecondsOverride;
        await MarkKeyChangedAsync(entity.Id);
        await _context.SaveChangesAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var result = new McpAccessKeyListItem
        {
            Id = entity.Id,
            Name = entity.Name,
            KeyPrefix = entity.KeyPrefix,
            IsActive = entity.IsActive && !entity.RevokedAt.HasValue &&
                (!entity.ExpiresAt.HasValue || entity.ExpiresAt > now),
            IsExpired = entity.ExpiresAt.HasValue && entity.ExpiresAt <= now,
            IsExpiringSoon = entity.ExpiresAt.HasValue && entity.ExpiresAt > now &&
                entity.ExpiresAt <= now.AddDays(7),
            ExpiresAt = entity.ExpiresAt,
            LastUsedAt = entity.LastUsedAt,
            AllowedTools = entity.AllowedTools,
            CorsAllowedOrigins = entity.CorsAllowedOrigins,
            SqlProvider = entity.SqlProvider,
            DbManagementId = entity.DbManagementId,
            TableWhitelist = entity.TableWhitelist,
            RateLimitMode = entity.RateLimitMode,
            PermitLimitOverride = entity.PermitLimitOverride,
            WindowSecondsOverride = entity.WindowSecondsOverride,
            CreatedAt = entity.CreatedAt
        };
        var policy = _securityPolicyRuntimeState.GetCurrent();
        ApplyEffectiveRateLimit(result, policy.KeyPermitLimit, policy.KeyWindowSeconds);
        return result;
    }

    public async Task<McpAccessKeyIssueResult?> RotateKeyAsync(
        int id,
        RotateMcpAccessKeyRequest request,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        if (request.GracePeriodMinutes is < 0 or > 1440)
            throw new ArgumentException("GracePeriodMinutes must be between 0 and 1440.");

        var oldKey = await _context.McpAccessKeys.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (oldKey is null)
            return null;
        if (!oldKey.IsActive || oldKey.RevokedAt.HasValue ||
            (oldKey.ExpiresAt.HasValue && oldKey.ExpiresAt <= DateTime.UtcNow))
            throw new InvalidOperationException("Only an active key can be rotated.");

        var plaintext = GenerateRawKey();
        var replacement = CreateEntity(new IssueMcpAccessKeyModel
        {
            Name = oldKey.Name,
            ExpiresAt = request.ExpiresAt,
            AllowedTools = oldKey.AllowedTools,
            CorsAllowedOrigins = oldKey.CorsAllowedOrigins,
            DbManagementId = oldKey.DbManagementId,
            TableWhitelist = oldKey.TableWhitelist,
            RateLimitMode = oldKey.RateLimitMode,
            PermitLimitOverride = oldKey.PermitLimitOverride,
            WindowSecondsOverride = oldKey.WindowSecondsOverride
        }, plaintext, actorId);
        _context.McpAccessKeys.Add(replacement);

        if (request.GracePeriodMinutes == 0)
        {
            oldKey.IsActive = false;
            oldKey.RevokedAt = DateTime.UtcNow;
            oldKey.RevokedBy = actorId;
        }
        else
        {
            var graceExpiry = DateTime.UtcNow.AddMinutes(request.GracePeriodMinutes);
            if (!oldKey.ExpiresAt.HasValue || oldKey.ExpiresAt > graceExpiry)
                oldKey.ExpiresAt = graceExpiry;
        }

        await MarkKeyChangedAsync(oldKey.Id);
        if (request.GracePeriodMinutes == 0)
            await MarkKeyRevokedAsync(oldKey.Id);
        await _context.SaveChangesAsync(cancellationToken);
        return CreateIssueResult(replacement, plaintext);
    }

    public async Task<McpAccessKeyIssueResult?> CloneKeyAsync(
        int id,
        CloneMcpAccessKeyRequest request,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Key name is required.", nameof(request.Name));

        var source = await _context.McpAccessKeys.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null)
            return null;

        var plaintext = GenerateRawKey();
        var clone = CreateEntity(new IssueMcpAccessKeyModel
        {
            Name = request.Name,
            ExpiresAt = request.ExpiresAt,
            AllowedTools = source.AllowedTools,
            CorsAllowedOrigins = source.CorsAllowedOrigins,
            DbManagementId = source.DbManagementId,
            TableWhitelist = source.TableWhitelist,
            RateLimitMode = source.RateLimitMode,
            PermitLimitOverride = source.PermitLimitOverride,
            WindowSecondsOverride = source.WindowSecondsOverride
        }, plaintext, actorId);
        _context.McpAccessKeys.Add(clone);
        await _context.SaveChangesAsync(cancellationToken);
        return CreateIssueResult(clone, plaintext);
    }

    public async Task<bool> RevokeKeyAsync(int id, string? actorId, CancellationToken cancellationToken = default)
    {
        var key = await _context.McpAccessKeys.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (key is null)
        {
            return false;
        }

        key.IsActive = false;
        key.RevokedAt = DateTime.UtcNow;
        key.RevokedBy = actorId;

        // Publish the tombstone before committing so a cache failure cannot leave a
        // successfully revoked key usable with stale validation data.
        await MarkKeyRevokedAsync(key.Id);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<McpAccessKeyValidationResult> ValidateAsync(string rawKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return new McpAccessKeyValidationResult { IsValid = false, Reason = "Missing key." };
        }

        var key = rawKey.Trim();
        var prefixLength = Math.Min(KeyPrefixLength, key.Length);
        var prefix = key[..prefixLength];

        var candidates = await _context.McpAccessKeys
            .AsNoTracking()
            .Where(x => x.KeyPrefix == prefix && x.IsActive && !x.RevokedAt.HasValue)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return new McpAccessKeyValidationResult { IsValid = false, Reason = "Key not found." };
        }

        var entity = candidates.FirstOrDefault(x => VerifyKey(key, x.KeyHash, _hmacSecret));

        if (entity is null)
        {
            return new McpAccessKeyValidationResult { IsValid = false, Reason = "Key not found." };
        }

        if (!entity.IsActive || entity.RevokedAt.HasValue)
        {
            return new McpAccessKeyValidationResult { IsValid = false, Reason = "Key revoked." };
        }

        if (entity.ExpiresAt.HasValue && entity.ExpiresAt.Value <= DateTime.UtcNow)
        {
            return new McpAccessKeyValidationResult { IsValid = false, Reason = "Key expired." };
        }

        return new McpAccessKeyValidationResult
        {
            IsValid = true,
            KeyId = entity.Id,
            Name = entity.Name,
            AllowedTools = entity.AllowedTools,
            CorsAllowedOrigins = entity.CorsAllowedOrigins,
            CorsAllowedOriginsSet = ParseCorsAllowedOrigins(entity.CorsAllowedOrigins),
            SqlProvider = entity.SqlProvider,
            DbManagementId = entity.DbManagementId,
            TableWhitelist = entity.TableWhitelist,
            ExpiresAt = entity.ExpiresAt,
            RateLimitMode = entity.RateLimitMode,
            PermitLimitOverride = entity.PermitLimitOverride,
            WindowSecondsOverride = entity.WindowSecondsOverride
        };
    }

    public async Task TouchLastUsedAsync(int keyId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.McpAccessKeys.FirstOrDefaultAsync(x => x.Id == keyId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string HashKey(string rawKey, byte[] hmacSecret)
    {
        return McpAccessKeyCacheKeys.ComputeKeyHash(rawKey, hmacSecret);
    }

    private McpAccessKey CreateEntity(
        IssueMcpAccessKeyModel request,
        string plaintext,
        string? actorId)
    {
        var prefixLength = Math.Min(KeyPrefixLength, plaintext.Length);
        return new McpAccessKey
        {
            Name = request.Name.Trim(),
            KeyPrefix = plaintext[..prefixLength],
            KeyHash = HashKey(plaintext, _hmacSecret),
            ExpiresAt = request.ExpiresAt,
            AllowedTools = NormalizeNullable(request.AllowedTools),
            CorsAllowedOrigins = NormalizeCorsAllowedOrigins(request.CorsAllowedOrigins),
            DbManagementId = request.DbManagementId,
            TableWhitelist = NormalizeNullable(request.TableWhitelist),
            RateLimitMode = request.RateLimitMode,
            PermitLimitOverride = request.PermitLimitOverride,
            WindowSecondsOverride = request.WindowSecondsOverride,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actorId,
            IsActive = true
        };
    }

    private static McpAccessKeyIssueResult CreateIssueResult(McpAccessKey entity, string plaintext)
        => new()
        {
            Id = entity.Id,
            Name = entity.Name,
            KeyPrefix = entity.KeyPrefix,
            PlaintextKey = plaintext,
            ExpiresAt = entity.ExpiresAt,
            AllowedTools = entity.AllowedTools,
            CorsAllowedOrigins = entity.CorsAllowedOrigins,
            SqlProvider = entity.SqlProvider,
            DbManagementId = entity.DbManagementId,
            TableWhitelist = entity.TableWhitelist,
            RateLimitMode = entity.RateLimitMode,
            PermitLimitOverride = entity.PermitLimitOverride,
            WindowSecondsOverride = entity.WindowSecondsOverride
        };

    private static void NormalizeAndValidateRateLimit(IssueMcpAccessKeyModel request)
    {
        (request.PermitLimitOverride, request.WindowSecondsOverride) =
            NormalizeAndValidateRateLimit(request.RateLimitMode, request.PermitLimitOverride, request.WindowSecondsOverride);
    }

    private static void NormalizeAndValidateRateLimit(UpdateMcpAccessKeyRequest request)
    {
        (request.PermitLimitOverride, request.WindowSecondsOverride) =
            NormalizeAndValidateRateLimit(request.RateLimitMode, request.PermitLimitOverride, request.WindowSecondsOverride);
    }

    private static (int? PermitLimit, int? WindowSeconds) NormalizeAndValidateRateLimit(
        McpKeyRateLimitMode mode,
        int? permitLimit,
        int? windowSeconds)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentException("Invalid rate limit mode.");

        if (mode != McpKeyRateLimitMode.Custom)
            return (null, null);

        if (!permitLimit.HasValue || permitLimit is < 1 or > 1_000_000)
            throw new ArgumentException("PermitLimitOverride must be between 1 and 1000000 in Custom mode.");
        if (!windowSeconds.HasValue || windowSeconds is < 1 or > 86_400)
            throw new ArgumentException("WindowSecondsOverride must be between 1 and 86400 in Custom mode.");

        return (permitLimit, windowSeconds);
    }

    private static void ApplyEffectiveRateLimit(
        McpAccessKeyListItem item,
        int defaultPermitLimit,
        int defaultWindowSeconds)
    {
        switch (item.RateLimitMode)
        {
            case McpKeyRateLimitMode.Inherit:
                item.EffectivePermitLimit = defaultPermitLimit;
                item.EffectiveWindowSeconds = defaultWindowSeconds;
                break;
            case McpKeyRateLimitMode.Custom:
                item.EffectivePermitLimit = item.PermitLimitOverride;
                item.EffectiveWindowSeconds = item.WindowSecondsOverride;
                break;
            case McpKeyRateLimitMode.Unlimited:
                item.EffectivePermitLimit = null;
                item.EffectiveWindowSeconds = null;
                break;
        }
    }

    private Task MarkKeyChangedAsync(int keyId)
        => _cache.SetAsync(
            McpAccessKeyCacheKeys.ForChangedKeyId(keyId),
            true,
            ChangedKeyRefreshExpiry,
            CancellationToken.None);

    private Task MarkKeyRevokedAsync(int keyId)
        => _cache.SetAsync(
            McpAccessKeyCacheKeys.ForRevokedKeyId(keyId),
            true,
            RevocationTombstoneExpiry,
            CancellationToken.None);

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool VerifyKey(string rawKey, string storedHash, byte[] hmacSecret)
    {
        var expected = HashKey(rawKey, hmacSecret);
        var expectedBytes = Convert.FromBase64String(expected);
        var storedBytes = Convert.FromBase64String(storedHash);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, storedBytes);
    }

    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string? NormalizeCorsAllowedOrigins(string? corsAllowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(corsAllowedOrigins))
        {
            return null;
        }

        var normalized = corsAllowedOrigins
            .Split(CorsOriginsSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeOrigin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? null : string.Join(',', normalized);
    }

    private static string NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Scheme)
            || string.IsNullOrWhiteSpace(parsed.Host))
        {
            throw new ArgumentException($"Invalid CORS origin: {origin}");
        }

        var portPart = parsed.IsDefaultPort ? string.Empty : $":{parsed.Port}";
        return $"{parsed.Scheme.ToLowerInvariant()}://{parsed.Host.ToLowerInvariant()}{portPart}";
    }

    private static HashSet<string>? ParseCorsAllowedOrigins(string? corsAllowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(corsAllowedOrigins))
        {
            return null;
        }

        return corsAllowedOrigins
            .Split(CorsOriginsSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
