using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Extensions;

/// <summary>
/// Registers a purpose-bound key ring without implicitly changing any legacy
/// ciphertext writer. Configuration errors fail at registration; no key bytes
/// or Base64 text are included in exceptions or diagnostic output.
/// </summary>
public static class HsSqlAgentSecretEncryptionServiceExtensions
{
    public static IServiceCollection AddHsSqlAgentSecretEncryption(
        this IServiceCollection services,
        SecretEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        // v3 requires both key rings regardless of feature use. Do not
        // silently degrade to legacy encryption on missing configuration.
        if (options.Purposes is null || options.Purposes.Count != 2 ||
            !options.Purposes.ContainsKey(nameof(SecretPurpose.DatabasePassword)) ||
            !options.Purposes.ContainsKey(nameof(SecretPurpose.DurableDmlPayload)))
            throw new InvalidOperationException(
                "v3 requires independent DatabasePassword and DurableDmlPayload encryption key rings.");

        try
        {
            var keys = new Dictionary<SecretPurpose, SecretKeySet>();
            foreach (var (purposeName, config) in options.Purposes)
            {
                if (!Enum.TryParse<SecretPurpose>(purposeName, ignoreCase: true, out var purpose)
                    || !Enum.IsDefined(purpose))
                    throw new InvalidOperationException("Unknown SecretEncryption purpose.");
                if (config is null || string.IsNullOrWhiteSpace(config.ActiveKeyId) || config.Keys is null)
                    throw new InvalidOperationException("SecretEncryption purpose needs an active key ID and keys.");

                var decoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var (keyId, value) in config.Keys)
                    decoded.Add(keyId, Convert.FromBase64String(value));

                if (!keys.TryAdd(purpose, new SecretKeySet
                {
                    ActiveKeyId = config.ActiveKeyId,
                    Keys = decoded
                }))
                    throw new InvalidOperationException("Duplicate SecretEncryption purpose.");
            }

            // This construction strictly validates sizes, key IDs, active IDs and
            // rejects reuse of key material across multiple key IDs/purposes.
            var ring = new SecretKeyRing(keys);
            services.TryAddSingleton<ISecretKeyRing>(ring);
            services.TryAddSingleton(new SecretEncryptionWritePolicy(true, true));
            services.TryAddSingleton<ISecretWriteRolloutPolicy>(
                sp => sp.GetRequiredService<SecretEncryptionWritePolicy>());
            // HMAC fingerprints remain a separate cryptographic primitive.
            // Reject direct or SHA256-derived HMAC key reuse without enabling
            // ANY legacy ciphertext decryption.
            services.TryAddSingleton<ISecretProtector>(sp =>
            {
                var hmac = sp.GetService<IOptions<McpKeySettings>>()?.Value.HmacSecretKey;
                if (!string.IsNullOrEmpty(hmac))
                {
                    var hmacBytes = Encoding.UTF8.GetBytes(hmac);
                    var derived = SHA256.HashData(hmacBytes);
                    try
                    {
                        foreach (var (purpose, set) in keys)
                        foreach (var keyId in set.Keys.Keys)
                        {
                            var aesKey = ring.GetDecryptionKey(purpose, keyId);
                            try
                            {
                                if (CryptographicOperations.FixedTimeEquals(aesKey, derived) ||
                                    (aesKey.Length == hmacBytes.Length &&
                                     CryptographicOperations.FixedTimeEquals(aesKey, hmacBytes)))
                                    throw new InvalidOperationException(
                                        "v3 AES keys must not reuse MCP HMAC key material.");
                            }
                            finally { CryptographicOperations.ZeroMemory(aesKey); }
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(derived);
                        CryptographicOperations.ZeroMemory(hmacBytes);
                    }
                }
                return new VersionedSecretProtector(sp.GetRequiredService<ISecretKeyRing>());
            });
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            // Never include the invalid key material in configuration diagnostics.
            throw new InvalidOperationException(
                "Invalid SecretEncryption key ring. Check key IDs, 32-byte Base64 keys and per-purpose separation.");
        }

        return services;
    }
}
