using System.Security.Cryptography;
using System.Text;

namespace Admin.Service.Models;

public static class McpAccessKeyCacheKeys
{
    private const string ValidationCachePrefix = "mcp_auth_v3_";
    private const string RevokedCachePrefix = "mcp_auth_revoked_v1_";
    private const string ChangedCachePrefix = "mcp_auth_changed_v1_";

    public static string ComputeKeyHash(string rawKey, byte[] hmacSecret)
    {
        var hash = HMACSHA256.HashData(hmacSecret, Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToBase64String(hash);
    }

    public static string ForRawKey(string rawKey, byte[] hmacSecret)
        => ForStoredHash(ComputeKeyHash(rawKey, hmacSecret));

    public static string ForStoredHash(string storedHash)
        => $"{ValidationCachePrefix}{storedHash}";

    public static string ForRevokedKeyId(int keyId)
        => $"{RevokedCachePrefix}{keyId}";

    public static string ForChangedKeyId(int keyId)
        => $"{ChangedCachePrefix}{keyId}";
}
