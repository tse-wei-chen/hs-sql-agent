using System.Security.Cryptography;
using System.Text;
using Common.Interfaces;

namespace Common.Services;

public sealed class CryptoService : ICryptoService
{
    private readonly Dictionary<string, (string Active, Dictionary<string, byte[]> Keys)> rings = new();
    public CryptoService(SecretProtectionSettings settings)
    {
        var material = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (purpose, ring) in new[] { ("db-password", settings.DatabasePasswords), ("dml-payload", settings.DmlPayloads) })
        {
            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (id, value) in ring.Keys)
            {
                if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                    throw new InvalidOperationException("Secret key IDs require 1-64 ASCII letters, digits, hyphens or underscores.");
                var bytes = DecodeKey(value);
                if (!material.Add(Convert.ToBase64String(bytes)))
                    throw new InvalidOperationException("Secret keys must be independent across IDs and purposes.");
                keys.Add(id, bytes);
            }
            if (!keys.ContainsKey(ring.ActiveKeyId)) throw new InvalidOperationException($"SecretProtection:{purpose} requires an active key.");
            rings.Add(purpose, (ring.ActiveKeyId, keys));
        }
        if (!material.Add(Convert.ToBase64String(DecodeKey(settings.DmlFingerprintKey))))
            throw new InvalidOperationException("DML fingerprint key must be independent of encryption keys.");
    }
    public static byte[] DecodeKey(string value)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { throw new InvalidOperationException("Secret protection keys require Base64 encoded 32-byte random values."); }
        if (bytes.Length != 32) throw new InvalidOperationException("Secret protection keys must be exactly 32 bytes.");
        return bytes;
    }
    private (string Id, byte[] Key) GetKey(string context, string? id)
    {
        var separator = context.IndexOf(':');
        if (separator < 1 || separator == context.Length - 1 || !rings.TryGetValue(context[..separator], out var ring))
            throw new CryptographicException("Invalid secret protection context.");
        id ??= ring.Active;
        if (!ring.Keys.TryGetValue(id, out var key)) throw new CryptographicException("Secret key is unavailable.");
        return (id, key);
    }

    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string? EncryptText(string? plainText, string context)
    {
        if (plainText is null) return null;

        var (id, key) = GetKey(context, null);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] tag = new byte[TagSize];
        byte[] cipherText = new byte[plainBytes.Length];

        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plainBytes, cipherText, tag, Encoding.UTF8.GetBytes($"v2:{id}:" + context));
        }
        finally { CryptographicOperations.ZeroMemory(plainBytes); }

        byte[] result = new byte[NonceSize + TagSize + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(cipherText, 0, result, NonceSize + TagSize, cipherText.Length);

        return $"v2:{id}:" + Convert.ToBase64String(result);
    }

    public string? DecryptText(string? cipherText, string context)
    {
        if (cipherText is null or "") return cipherText;

        var parts = cipherText.Split(':');
        if (parts.Length != 3 || parts[0] != "v2") throw new CryptographicException("Unsupported secret envelope.");
        var (id, key) = GetKey(context, parts[1]);
        byte[] fullCipher;
        try { fullCipher = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { throw new CryptographicException("Invalid secret envelope."); }

        if (fullCipher.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Invalid cipher text.");
        }

        var nonce = fullCipher.AsSpan(0, NonceSize);
        var tag = fullCipher.AsSpan(NonceSize, TagSize);
        var cipherBytes = fullCipher.AsSpan(NonceSize + TagSize);

        byte[] decryptedBytes = new byte[cipherBytes.Length];

        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, cipherBytes, tag, decryptedBytes, Encoding.UTF8.GetBytes($"v2:{id}:" + context));
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        finally { CryptographicOperations.ZeroMemory(decryptedBytes); }
    }
}
