using System.Security.Cryptography;
using Common.Interfaces;

namespace Common.Services;

/// <summary>v3-only durable approval reader; product v2 ciphertext is not supported.</summary>
public static class DurableDmlPayloadCiphertextReader
{
    public static string? Decrypt(
        string? ciphertext,
        string requestId,
        ICryptoService legacyCrypto,
        byte[] legacyHmacSecret,
        ISecretProtector? secretProtector = null)
    {
        _ = legacyCrypto;
        _ = legacyHmacSecret;
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith("v2:", StringComparison.Ordinal)
            || secretProtector is null)
            throw new CryptographicException("v3 requires an authenticated durable DML payload and configured key ring.");
        return secretProtector.Unprotect(ciphertext, SecretPurpose.DurableDmlPayload, requestId);
    }
}
