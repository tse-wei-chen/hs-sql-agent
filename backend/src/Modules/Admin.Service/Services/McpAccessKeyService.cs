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
    ICacheService cache) : IMcpAccessKeyService
{
    private const int KeyPrefixLength = 8;
    private static readonly TimeSpan RevocationTombstoneExpiry = TimeSpan.FromMinutes(10);
    private static readonly char[] CorsOriginsSeparators = [',', ';', '\n', '\r'];
    private readonly IAdminContext _context = context;
    private readonly byte[] _hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
    private readonly ICryptoService _cryptoService = cryptoService;
    private readonly ICacheService _cache = cache;

    public async Task<McpAccessKeyIssueResult> IssueKeyAsync(
        IssueMcpAccessKeyModel request,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Key name is required.", nameof(request.Name));
        }

        var normalizedCorsAllowedOrigins = NormalizeCorsAllowedOrigins(request.CorsAllowedOrigins);

        var plaintext = GenerateRawKey();
        var keyHash = HashKey(plaintext, _hmacSecret);
        var prefixLength = Math.Min(KeyPrefixLength, plaintext.Length);

        var entity = new McpAccessKey
        {
            Name = request.Name.Trim(),
            KeyPrefix = plaintext[..prefixLength],
            KeyHash = keyHash,
            ExpiresAt = request.ExpiresAt,
            AllowedTools = string.IsNullOrWhiteSpace(request.AllowedTools) ? null : request.AllowedTools.Trim(),
            CorsAllowedOrigins = normalizedCorsAllowedOrigins,
            DbManagementId = request.DbManagementId,
            TableWhitelist = string.IsNullOrWhiteSpace(request.TableWhitelist) ? null : request.TableWhitelist.Trim(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actorId,
            IsActive = true
        };

        _context.McpAccessKeys.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return new McpAccessKeyIssueResult
        {
            Id = entity.Id,
            Name = entity.Name,
            KeyPrefix = entity.KeyPrefix,
            PlaintextKey = plaintext,
            ExpiresAt = entity.ExpiresAt,
            AllowedTools = entity.AllowedTools,
            CorsAllowedOrigins = entity.CorsAllowedOrigins,
            SqlProvider = entity.SqlProvider,
            TableWhitelist = entity.TableWhitelist,
        };
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
                ExpiresAt = x.ExpiresAt,
                LastUsedAt = x.LastUsedAt,
                AllowedTools = x.AllowedTools,
                CorsAllowedOrigins = x.CorsAllowedOrigins,
                SqlProvider = x.SqlProvider,
                DbManagementId = x.DbManagementId,
                TableWhitelist = x.TableWhitelist,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var dbIds = items
            .Where(x => x.DbManagementId.HasValue)
            .Select(x => x.DbManagementId!.Value)
            .Distinct()
            .ToArray();

        if (dbIds.Length == 0)
        {
            return items;
        }

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

        return items;
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

        await _context.SaveChangesAsync(cancellationToken);
        // The stored HMAC is also the validation cache identity, so revocation can
        // invalidate the exact cached key without retaining the plaintext secret.
        // Keep the tombstone longer than the validation cache TTL to close the race
        // where an in-flight validation read the active row before this commit.
        await _cache.SetAsync(
            McpAccessKeyCacheKeys.ForRevokedKeyId(key.Id),
            true,
            absoluteExpireTime: RevocationTombstoneExpiry,
            CancellationToken.None);
        await _cache.RemoveAsync(McpAccessKeyCacheKeys.ForStoredHash(key.KeyHash), CancellationToken.None);
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
            ExpiresAt = entity.ExpiresAt
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
