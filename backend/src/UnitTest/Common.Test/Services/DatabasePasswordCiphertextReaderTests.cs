using System.Security.Cryptography;
using System.Text;
using Common.Interfaces;
using Common.Services;
using Xunit;

namespace Common.Test.Services;

public sealed class DatabasePasswordCiphertextReaderTests
{
    private static byte[] LegacyKey => Encoding.UTF8.GetBytes("legacy-hmac-key-at-least-32-bytes-long");

    private static ISecretProtector Protector()
    {
        return new VersionedSecretProtector(new SecretKeyRing(
            new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DatabasePassword] = new SecretKeySet
                {
                    ActiveKeyId = "db_v2",
                    Keys = new Dictionary<string, byte[]>
                    {
                        ["db_v2"] = Enumerable.Repeat((byte)0x83, 32).ToArray()
                    }
                }
            }), LegacyKey);
    }

    [Fact]
    public void LegacyPasswordsRemainReadableWithoutKeyRing()
    {
        var crypto = new CryptoService();
        var old = crypto.EncryptText("db-password", LegacyKey);
        Assert.Equal("db-password", DatabasePasswordCiphertextReader.Decrypt(old, 42, crypto, LegacyKey));
    }

    [Fact]
    public void V2RequiresConfiguredKeyRingAndMatchingDatabaseId()
    {
        var protector = Protector();
        var crypto = new CryptoService();
        var encrypted = protector.Protect("password", SecretPurpose.DatabasePassword, "42");
        Assert.Equal("password", DatabasePasswordCiphertextReader.Decrypt(encrypted, 42, crypto, LegacyKey, protector));
        Assert.ThrowsAny<CryptographicException>(() =>
            DatabasePasswordCiphertextReader.Decrypt(encrypted, 43, crypto, LegacyKey, protector));
        Assert.ThrowsAny<CryptographicException>(() =>
            DatabasePasswordCiphertextReader.Decrypt(encrypted, 42, crypto, LegacyKey));
        Assert.ThrowsAny<CryptographicException>(() =>
            DatabasePasswordCiphertextReader.Decrypt(encrypted, 0, crypto, LegacyKey, protector));
    }

    [Fact]
    public void UnsupportedVersionNeverDowngrades()
    {
        Assert.ThrowsAny<CryptographicException>(() =>
            DatabasePasswordCiphertextReader.Decrypt("v3:key:AA==", 42,
                new CryptoService(), LegacyKey, Protector()));
    }
}
