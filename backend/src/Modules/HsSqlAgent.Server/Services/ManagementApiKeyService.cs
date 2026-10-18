using System.Security.Cryptography;
using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Services;

/// <summary>Explicit allowlist. Machine keys cannot administer identities, policies, or other Management API keys.</summary>
public static class ManagementApiKeyPermissions
{
    public static readonly IReadOnlyList<string> Grantable =
    [
        "/runtime/custom-tools.view", "/runtime/custom-tools.create",
        "/runtime/custom-tools.edit", "/runtime/custom-tools.delete",
        "/runtime/mcp-keys.view", "/runtime/mcp-keys.create",
        "/runtime/mcp-keys.edit", "/runtime/mcp-keys.revoke",
        "/runtime/db-management.view", "/runtime/db-management.create",
        "/runtime/db-management.edit", "/runtime/db-management.delete",
        "/runtime/db-management/semantic.view", "/runtime/db-management/semantic.edit",
        "/runtime/audit.view", "/runtime/audit.export",
        "/runtime/operability.view"
    ];

    private static readonly HashSet<string> Allowed = new(Grantable, StringComparer.Ordinal);

    public static string[] Validate(IEnumerable<string>? permissions)
    {
        if (permissions is null) throw new ArgumentException("At least one permission is required.");
        var entries = permissions.ToArray();
        if (entries.Length == 0 || entries.Length > Allowed.Count ||
            entries.Any(p => string.IsNullOrEmpty(p) || !Allowed.Contains(p)))
            throw new ArgumentException("Permissions must be explicitly selected from the Management API catalog.");
        return entries.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static bool IsGrantable(string permission) => Allowed.Contains(permission);
}

public sealed record ManagementApiKeyRequest(string Name, DateTime ExpiresAt, string[] Permissions);
public sealed record ManagementApiKeyView(int Id, string Name, string KeyPrefix,
    string[] Permissions, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? LastUsedAt, DateTime? RevokedAt, string? CreatedBy);
public sealed record IssuedManagementApiKey(ManagementApiKeyView Key, string PlaintextKey);
public sealed record ValidManagementApiKey(int Id, string Name, string[] Permissions);

public interface IManagementApiKeyService
{
    Task<IReadOnlyList<ManagementApiKeyView>> ListAsync(CancellationToken cancellationToken);
    Task<IssuedManagementApiKey> IssueAsync(ManagementApiKeyRequest request, string? actor, CancellationToken cancellationToken);
    Task<IssuedManagementApiKey?> RotateAsync(int id, string? actor, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(int id, string? actor, CancellationToken cancellationToken);
    Task<ValidManagementApiKey?> ValidateAsync(string token, CancellationToken cancellationToken);
}

public sealed class ManagementApiKeyService(IAdminContext context) : IManagementApiKeyService
{
    public const string TokenPrefix = "hsa_mgmt_";
    private const int LookupPrefixLength = 20;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(365);

    public async Task<IReadOnlyList<ManagementApiKeyView>> ListAsync(CancellationToken cancellationToken)
        => (await context.ManagementApiKeys.AsNoTracking().OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken)).Select(ToView).ToArray();

    public async Task<IssuedManagementApiKey> IssueAsync(
        ManagementApiKeyRequest request, string? actor, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            throw new ArgumentException("Key name must have between 1 and 100 characters.");
        var grants = ManagementApiKeyPermissions.Validate(request.Permissions);
        var now = DateTime.UtcNow;
        var expiration = request.ExpiresAt.ToUniversalTime();
        if (expiration <= now || expiration > now + MaximumLifetime)
            throw new ArgumentException("Expiry must be in the future and within 365 days.");
        var result = Create(request.Name.Trim(), expiration, grants, actor, now);
        context.ManagementApiKeys.Add(result.Entity);
        await context.SaveChangesAsync(cancellationToken);
        return new(ToView(result.Entity), result.RawKey);
    }

    public async Task<IssuedManagementApiKey?> RotateAsync(
        int id, string? actor, CancellationToken cancellationToken)
    {
        var entity = await context.ManagementApiKeys.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return null;
        var now = DateTime.UtcNow;
        if (entity.RevokedAt is not null || entity.ExpiresAt <= now)
            throw new InvalidOperationException("Only active keys can be rotated.");
        var replacement = Create(entity.Name, entity.ExpiresAt,
            ManagementApiKeyPermissions.Validate(JsonSerializer.Deserialize<string[]>(entity.PermissionsJson)), actor, now);
        entity.RevokedAt = now;
        entity.RevokedBy = actor;
        context.ManagementApiKeys.Add(replacement.Entity);
        // Both changes commit in one SaveChanges transaction. No overlapping key grace period.
        await context.SaveChangesAsync(cancellationToken);
        return new(ToView(replacement.Entity), replacement.RawKey);
    }

    public async Task<bool> RevokeAsync(int id, string? actor, CancellationToken cancellationToken)
    {
        var entity = await context.ManagementApiKeys.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return false;
        if (entity.RevokedAt is not null) return true;
        entity.RevokedAt = DateTime.UtcNow;
        entity.RevokedBy = actor;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ValidManagementApiKey?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal)
            || token.Length != TokenPrefix.Length + 43) return null;

        var prefix = token[..LookupPrefixLength];
        var candidates = await context.ManagementApiKeys
            .AsNoTracking().Where(x => x.KeyPrefix == prefix && x.RevokedAt == null
                && x.ExpiresAt > DateTime.UtcNow).ToListAsync(cancellationToken);
        var suppliedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        foreach (var item in candidates)
        {
            byte[] expected;
            try { expected = Convert.FromHexString(item.KeyHash); }
            catch (FormatException) { continue; }
            if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expected)) continue;
            var grants = JsonSerializer.Deserialize<string[]>(item.PermissionsJson);
            if (grants is null || grants.Length == 0 || grants.Any(p => !ManagementApiKeyPermissions.IsGrantable(p)))
                return null;

            if (item.LastUsedAt is null || item.LastUsedAt < DateTime.UtcNow.AddMinutes(-1))
                await context.ManagementApiKeys.Where(x => x.Id == item.Id && x.RevokedAt == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.LastUsedAt, DateTime.UtcNow), cancellationToken);

            return new(item.Id, item.Name, grants);
        }
        return null;
    }

    private static (ManagementApiKey Entity, string RawKey) Create(
        string name, DateTime expiresAt, string[] grants, string? actor, DateTime now)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var rawKey = TokenPrefix + secret;
        return (new ManagementApiKey
        {
            Name = name,
            KeyPrefix = rawKey[..LookupPrefixLength],
            KeyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey))),
            PermissionsJson = JsonSerializer.Serialize(grants),
            CreatedBy = actor,
            CreatedAt = now,
            ExpiresAt = expiresAt
        }, rawKey);
    }

    private static ManagementApiKeyView ToView(ManagementApiKey x)
        => new(x.Id, x.Name, x.KeyPrefix,
            JsonSerializer.Deserialize<string[]>(x.PermissionsJson) ?? [],
            x.CreatedAt, x.ExpiresAt, x.LastUsedAt, x.RevokedAt, x.CreatedBy);
}
