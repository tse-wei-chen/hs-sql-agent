using System.Globalization;
using System.Security.Cryptography;
using Common.Interfaces;

namespace Common.Services;

/// <summary>v3-only encrypted database credential reader. Never decrypt product-v2 data.</summary>
public static class DatabasePasswordCiphertextReader
{
    public static string? Decrypt(
        string? ciphertext,
        int dbManagementId,
        ICryptoService legacyCrypto,
        byte[] legacyHmacSecret,
        ISecretProtector? secretProtector = null)
    {
        _ = legacyCrypto;
        _ = legacyHmacSecret;
        if (ciphertext is null || ciphertext.Length == 0) return ciphertext;
        if (!ciphertext.StartsWith("v2:", StringComparison.Ordinal) || secretProtector is null || dbManagementId <= 0)
            throw new CryptographicException("v3 requires an authenticated database credential and configured key ring.");
        return secretProtector.Unprotect(ciphertext, SecretPurpose.DatabasePassword,
            dbManagementId.ToString(CultureInfo.InvariantCulture));
    }
}
