using System.Security.Cryptography;
using Common.Interfaces;

namespace Common.Services;

/// <summary>
/// Immutable, locally-configured encryption key inventory. Load each key from a
/// protected configuration source, not the Admin Store. Old keys remain read-only
/// until all dependent ciphertext has been migrated.
/// </summary>
public sealed class SecretKeySet
{
    public required string ActiveKeyId { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Keys { get; init; }
}

public sealed class SecretKeyRing : ISecretKeyRing
{
    private readonly IReadOnlyDictionary<SecretPurpose, (string ActiveKeyId, IReadOnlyDictionary<string, byte[]> Keys)> _sets;

    public SecretKeyRing(IReadOnlyDictionary<SecretPurpose, SecretKeySet> keySets)
    {
        ArgumentNullException.ThrowIfNull(keySets);
        if (keySets.Count == 0)
            throw new ArgumentException("At least one purpose-specific encryption key set is required.", nameof(keySets));

        var sets = new Dictionary<SecretPurpose, (string, IReadOnlyDictionary<string, byte[]>)>();
        var observedKeyMaterial = new List<byte[]>();
        foreach (var (purpose, config) in keySets)
        {
            ValidatePurpose(purpose);
            ArgumentNullException.ThrowIfNull(config);
            ValidateKeyId(config.ActiveKeyId);
            ArgumentNullException.ThrowIfNull(config.Keys);
            if (config.Keys.Count == 0)
                throw new ArgumentException("An encryption purpose needs at least one key.", nameof(keySets));

            var keyCopies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (keyId, key) in config.Keys)
            {
                ValidateKeyId(keyId);
                if (key is not { Length: 32 })
                    throw new ArgumentException("Each AES-256-GCM encryption key must contain exactly 32 random bytes.", nameof(keySets));
                var copy = (byte[])key.Clone();
                // Do not permit reuse of the same key bytes across purpose-specific key IDs.
                if (observedKeyMaterial.Any(existing => CryptographicOperations.FixedTimeEquals(existing, copy)))
                    throw new ArgumentException("Encryption keys must not be reused across key IDs or purposes.", nameof(keySets));
                observedKeyMaterial.Add(copy);
                keyCopies.Add(keyId, copy);
            }

            if (!keyCopies.ContainsKey(config.ActiveKeyId))
                throw new ArgumentException("Active encryption key ID must exist in its purpose-specific key set.", nameof(keySets));
            sets.Add(purpose, (config.ActiveKeyId, keyCopies));
        }

        _sets = sets;
    }

    public (string KeyId, byte[] Key) GetActiveKey(SecretPurpose purpose)
    {
        var set = RequireSet(purpose);
        return (set.ActiveKeyId, (byte[])set.Keys[set.ActiveKeyId].Clone());
    }

    public byte[] GetDecryptionKey(SecretPurpose purpose, string keyId)
    {
        var set = RequireSet(purpose);
        ValidateKeyId(keyId);
        if (!set.Keys.TryGetValue(keyId, out var key))
            throw new CryptographicException("Encryption key is not available.");
        return (byte[])key.Clone();
    }

    private (string ActiveKeyId, IReadOnlyDictionary<string, byte[]> Keys) RequireSet(SecretPurpose purpose)
    {
        ValidatePurpose(purpose);
        if (!_sets.TryGetValue(purpose, out var set))
            throw new CryptographicException("Encryption key purpose is not configured.");
        return set;
    }

    internal static void ValidatePurpose(SecretPurpose purpose)
    {
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose), "Unknown secret purpose.");
    }

    internal static void ValidateKeyId(string? keyId)
    {
        if (string.IsNullOrEmpty(keyId) || keyId.Length > 64 ||
            keyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Encryption key IDs must be 1-64 ASCII alphanumeric, dash or underscore characters.");
    }
}
