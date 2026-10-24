using System.Security.Cryptography;
using System.Text;
using Common.Interfaces;

namespace Common.Services;

/// <summary>
/// Versioned AES-256-GCM envelope: v2:{key-id}:{Base64(nonce[12] + tag[16] + ciphertext)}.
/// AAD binds the version, purpose and immutable record ID. The old unversioned
/// Base64 layout is accepted for decryption ONLY with an explicitly configured
/// legacy key; all new encryption emits v2.
/// </summary>
public sealed class VersionedSecretProtector : ISecretProtector
{
    private const string Prefix = "v2:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly ISecretKeyRing _keyRing;
    private readonly ICryptoService _legacyCrypto;
    private readonly byte[]? _legacyKey;

    public VersionedSecretProtector(ISecretKeyRing keyRing, byte[]? legacyKey = null, ICryptoService? legacyCrypto = null)
    {
        _keyRing = keyRing ?? throw new ArgumentNullException(nameof(keyRing));
        if (legacyKey is { Length: 0 })
            throw new ArgumentException("Legacy decryption key cannot be empty.", nameof(legacyKey));
        _legacyKey = legacyKey is null ? null : (byte[])legacyKey.Clone();
        _legacyCrypto = legacyCrypto ?? new CryptoService();
    }

    public string? Protect(string? plaintext, SecretPurpose purpose, string recordId)
    {
        var aad = BuildAad(purpose, recordId);
        // Whitespace-only passwords are still credentials, not plaintext sentinels.
        if (plaintext is null || plaintext.Length == 0) return plaintext;

        var (keyId, key) = _keyRing.GetActiveKey(purpose);
        try
        {
            SecretKeyRing.ValidateKeyId(keyId);
            if (key.Length != 32)
                throw new CryptographicException("Encryption key must be 32 bytes.");

            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                byte[] ciphertext = new byte[plainBytes.Length];
                byte[] tag = new byte[TagSize];
                using (var aes = new AesGcm(key, TagSize))
                    aes.Encrypt(nonce, plainBytes, ciphertext, tag, aad);

                byte[] envelope = new byte[NonceSize + TagSize + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, envelope, 0, NonceSize);
                Buffer.BlockCopy(tag, 0, envelope, NonceSize, TagSize);
                Buffer.BlockCopy(ciphertext, 0, envelope, NonceSize + TagSize, ciphertext.Length);
                return Prefix + keyId + ":" + Convert.ToBase64String(envelope);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string? Unprotect(string? ciphertext, SecretPurpose purpose, string recordId)
    {
        var aad = BuildAad(purpose, recordId);
        if (ciphertext is null || ciphertext.Length == 0) return ciphertext;
        if (string.IsNullOrWhiteSpace(ciphertext))
            throw new CryptographicException("Malformed secret envelope.");

        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Legacy ciphertexts are plain Base64 with no colon. Unknown/malformed
            // future envelope versions must never silently downgrade to legacy.
            if (ciphertext.Contains(':') || _legacyKey is null)
                throw new CryptographicException("Unsupported or unavailable secret envelope.");
            try
            {
                return _legacyCrypto.DecryptText(ciphertext, _legacyKey);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("Malformed legacy ciphertext.", ex);
            }
        }

        var keySeparator = ciphertext.IndexOf(':', Prefix.Length);
        if (keySeparator < 0)
            throw new CryptographicException("Malformed secret envelope.");

        var keyId = ciphertext[Prefix.Length..keySeparator];
        try { SecretKeyRing.ValidateKeyId(keyId); }
        catch (ArgumentException ex) { throw new CryptographicException("Invalid encryption key ID.", ex); }

        byte[] payload;
        try { payload = Convert.FromBase64String(ciphertext[(keySeparator + 1)..]); }
        catch (FormatException ex) { throw new CryptographicException("Malformed secret envelope.", ex); }
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("Malformed secret envelope.");

        var key = _keyRing.GetDecryptionKey(purpose, keyId);
        try
        {
            if (key.Length != 32)
                throw new CryptographicException("Encryption key must be 32 bytes.");

            byte[] plainBytes = new byte[payload.Length - NonceSize - TagSize];
            try
            {
                using (var aes = new AesGcm(key, TagSize))
                    aes.Decrypt(
                        payload.AsSpan(0, NonceSize),
                        payload.AsSpan(NonceSize + TagSize),
                        payload.AsSpan(NonceSize, TagSize),
                        plainBytes,
                        aad);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string? Reprotect(string? ciphertext, SecretPurpose purpose, string recordId)
    {
        var plaintext = Unprotect(ciphertext, purpose, recordId);
        return Protect(plaintext, purpose, recordId);
    }

    private static byte[] BuildAad(SecretPurpose purpose, string recordId)
    {
        SecretKeyRing.ValidatePurpose(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        if (recordId.Length > 1024)
            throw new ArgumentException("Secret record ID is too long.", nameof(recordId));

        // Byte-encoded ID is unambiguous, including when the ID contains separators.
        return Encoding.UTF8.GetBytes(
            "hs-sql-agent:secret:v2:" + purpose + ":" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(recordId)));
    }
}
