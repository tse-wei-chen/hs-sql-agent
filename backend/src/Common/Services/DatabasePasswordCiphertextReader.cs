using System.Globalization;
using System.Security.Cryptography;
using Common.Interfaces;

namespace Common.Services;

/// <summary>Shared database credential reader for legacy and v2 ciphertexts.</summary>
public static class DatabasePasswordCiphertextReader
{
    public static string? Decrypt(
        string? ciphertext,
        int dbManagementId,
        ICryptoService legacyCrypto,
        byte[] legacyHmacSecret,
        ISecretProtector? secretProtector = null)
    {
        ArgumentNullException.ThrowIfNull(legacyCrypto);
        ArgumentNullException.ThrowIfNull(legacyHmacSecret);
        if (ciphertext is null || ciphertext.Length == 0)
            return legacyCrypto.DecryptText(ciphertext, legacyHmacSecret);
        if (ciphertext.StartsWith("v2:", StringComparison.Ordinal))
        {
            if (secretProtector is null || dbManagementId <= 0)
                throw new CryptographicException(
                    "Versioned database credentials require an active key ring and stable database ID.");
            return secretProtector.Unprotect(
                ciphertext, SecretPurpose.DatabasePassword,
                dbManagementId.ToString(CultureInfo.InvariantCulture));
        }
        if (ciphertext.Contains(':'))
            throw new CryptographicException("Unsupported database credential envelope.");
        return legacyCrypto.DecryptText(ciphertext, legacyHmacSecret);
    }
}
