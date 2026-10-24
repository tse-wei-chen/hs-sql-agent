namespace Common.Interfaces;

/// <summary>
/// Persisted secret purposes. A ciphertext must never be decrypted for a different
/// purpose, even when both purposes are backed by valid encryption key material.
/// </summary>
public enum SecretPurpose
{
    DatabasePassword = 1,
    DurableDmlPayload = 2,
    ExtraSettings = 3
}

/// <summary>
/// Versioned authenticated secret encryption bound to a stable, immutable record ID.
/// Legacy ciphertexts can be read only when explicitly configured with the old secret.
/// </summary>
public interface ISecretProtector
{
    string? Protect(string? plaintext, SecretPurpose purpose, string recordId);
    string? Unprotect(string? ciphertext, SecretPurpose purpose, string recordId);
    string? Reprotect(string? ciphertext, SecretPurpose purpose, string recordId);
}

/// <summary>
/// Resolves encryption keys by purpose and key ID. Callers must not use MCP HMAC
/// credentials as a new encryption key.
/// </summary>
public interface ISecretKeyRing
{
    (string KeyId, byte[] Key) GetActiveKey(SecretPurpose purpose);
    byte[] GetDecryptionKey(SecretPurpose purpose, string keyId);
}
