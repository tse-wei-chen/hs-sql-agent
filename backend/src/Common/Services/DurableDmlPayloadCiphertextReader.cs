using System.Security.Cryptography;
using Common.Interfaces;

namespace Common.Services;

/// <summary>
/// Durable approval payload read boundary. It does not alter execution-state
/// transitions, approval fingerprints, target commit fencing or legacy writes.
/// </summary>
public static class DurableDmlPayloadCiphertextReader
{
    public static string? Decrypt(
        string? ciphertext,
        string requestId,
        ICryptoService legacyCrypto,
        byte[] legacyHmacSecret,
        ISecretProtector? secretProtector = null)
    {
        ArgumentNullException.ThrowIfNull(legacyCrypto);
        ArgumentNullException.ThrowIfNull(legacyHmacSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (ciphertext is null || ciphertext.Length == 0)
            return legacyCrypto.DecryptText(ciphertext, legacyHmacSecret);

        if (ciphertext.StartsWith("v2:", StringComparison.Ordinal))
        {
            if (secretProtector is null)
                throw new CryptographicException(
                    "Versioned approval payload requires an active encryption key ring.");
            return secretProtector.Unprotect(
                ciphertext, SecretPurpose.DurableDmlPayload, requestId);
        }

        if (ciphertext.Contains(':'))
            throw new CryptographicException("Unsupported approval payload envelope.");

        return legacyCrypto.DecryptText(ciphertext, legacyHmacSecret);
    }
}
