using System.Security.Cryptography;
using System.Text;
using Common.Interfaces;
using Common.Services;
using Xunit;

namespace Common.Test.Services;

public class VersionedSecretProtectorTests
{
    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static ISecretKeyRing Ring(string activeKeyId = "db_2026", bool keepOldKey = false)
    {
        var dbKeys = new Dictionary<string, byte[]> { [activeKeyId] = Key(0x11) };
        if (keepOldKey)
            dbKeys["db_2025"] = Key(0x33);
        return new SecretKeyRing(new Dictionary<SecretPurpose, SecretKeySet>
        {
            [SecretPurpose.DatabasePassword] = new SecretKeySet
            {
                ActiveKeyId = activeKeyId,
                Keys = dbKeys
            },
            [SecretPurpose.DurableDmlPayload] = new SecretKeySet
            {
                ActiveKeyId = "dml_2026",
                Keys = new Dictionary<string, byte[]> { ["dml_2026"] = Key(0x22) }
            }
        });
    }

    [Fact]
    public void RoundTrip_UsesRandomNonceAndAad()
    {
        var protector = new VersionedSecretProtector(Ring());
        var first = protector.Protect("🔐 connection-password", SecretPurpose.DatabasePassword, "db/42");
        var second = protector.Protect("🔐 connection-password", SecretPurpose.DatabasePassword, "db/42");

        Assert.NotNull(first);
        Assert.StartsWith("v2:db_2026:", first);
        Assert.NotEqual(first, second);
        Assert.Equal("🔐 connection-password", protector.Unprotect(first, SecretPurpose.DatabasePassword, "db/42"));
        Assert.Equal("🔐 connection-password", protector.Unprotect(second, SecretPurpose.DatabasePassword, "db/42"));
    }

    [Fact]
    public void WrongRecordIdOrPurposeCannotDecrypt()
    {
        var protector = new VersionedSecretProtector(Ring());
        var cipher = protector.Protect("secret", SecretPurpose.DatabasePassword, "42")!;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(cipher, SecretPurpose.DatabasePassword, "43"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(cipher, SecretPurpose.DurableDmlPayload, "42"));
    }

    [Fact]
    public void MutatedTagAndTruncatedEnvelopeFailClosed()
    {
        var protector = new VersionedSecretProtector(Ring());
        var cipher = protector.Protect("secret", SecretPurpose.DatabasePassword, "42")!;
        var segments = cipher.Split(':');
        var raw = Convert.FromBase64String(segments[2]);
        raw[12] ^= 0x01;
        var changed = segments[0] + ":" + segments[1] + ":" + Convert.ToBase64String(raw);
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(changed, SecretPurpose.DatabasePassword, "42"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v2:db_2026:AA==", SecretPurpose.DatabasePassword, "42"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v3:db_2026:AA==", SecretPurpose.DatabasePassword, "42"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v2:missing:AA==", SecretPurpose.DatabasePassword, "42"));
    }

    [Fact]
    public void KeyRotationKeepsOldKeyReadableAndReprotectsWithNewActiveKey()
    {
        var before = new VersionedSecretProtector(new SecretKeyRing(
            new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DatabasePassword] = new SecretKeySet
                {
                    ActiveKeyId = "db_2025",
                    Keys = new Dictionary<string, byte[]> { ["db_2025"] = Key(0x33) }
                }
            }));
        var oldCipher = before.Protect("password", SecretPurpose.DatabasePassword, "42")!;

        var after = new VersionedSecretProtector(Ring(keepOldKey: true));
        Assert.Equal("password", after.Unprotect(oldCipher, SecretPurpose.DatabasePassword, "42"));
        var rotated = after.Reprotect(oldCipher, SecretPurpose.DatabasePassword, "42")!;
        Assert.StartsWith("v2:db_2026:", rotated);
        Assert.Equal("password", after.Unprotect(rotated, SecretPurpose.DatabasePassword, "42"));
        Assert.ThrowsAny<CryptographicException>(() => new VersionedSecretProtector(Ring())
            .Unprotect(oldCipher, SecretPurpose.DatabasePassword, "42"));
    }

    [Fact]
    public void LegacyDecryptionIsOptInAndCanBeReprotected()
    {
        var legacyKey = Encoding.UTF8.GetBytes("legacy-hmac-key-at-least-32-bytes-long");
        var legacy = new CryptoService().EncryptText("old-password", legacyKey)!;
        var withoutLegacy = new VersionedSecretProtector(Ring());
        Assert.ThrowsAny<CryptographicException>(() => withoutLegacy.Unprotect(legacy, SecretPurpose.DatabasePassword, "42"));

        var protector = new VersionedSecretProtector(Ring(), legacyKey);
        Assert.Equal("old-password", protector.Unprotect(legacy, SecretPurpose.DatabasePassword, "42"));
        var migrated = protector.Reprotect(legacy, SecretPurpose.DatabasePassword, "42")!;
        Assert.StartsWith("v2:db_2026:", migrated);
        Assert.Equal("old-password", withoutLegacy.Unprotect(migrated, SecretPurpose.DatabasePassword, "42"));
    }

    [Fact]
    public void RingRejectsShortReusedAndMutableKeys()
    {
        Assert.Throws<ArgumentException>(() => new SecretKeyRing(
            new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DatabasePassword] = new SecretKeySet
                {
                    ActiveKeyId = "id",
                    Keys = new Dictionary<string, byte[]> { ["id"] = new byte[16] }
                }
            }));
        Assert.Throws<ArgumentException>(() => new SecretKeyRing(
            new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DatabasePassword] = new SecretKeySet
                {
                    ActiveKeyId = "db",
                    Keys = new Dictionary<string, byte[]> { ["db"] = Key(5) }
                },
                [SecretPurpose.DurableDmlPayload] = new SecretKeySet
                {
                    ActiveKeyId = "dml",
                    Keys = new Dictionary<string, byte[]> { ["dml"] = Key(5) }
                }
            }));

        var callerKey = Key(0x11);
        var ring = new SecretKeyRing(new Dictionary<SecretPurpose, SecretKeySet>
        {
            [SecretPurpose.DatabasePassword] = new SecretKeySet
            {
                ActiveKeyId = "id",
                Keys = new Dictionary<string, byte[]> { ["id"] = callerKey }
            }
        });
        callerKey[0] = 0;
        var returned = ring.GetActiveKey(SecretPurpose.DatabasePassword);
        Assert.Equal(0x11, returned.Key[0]);
        returned.Key[0] = 0;
        Assert.Equal(0x11, ring.GetDecryptionKey(SecretPurpose.DatabasePassword, "id")[0]);
    }

    [Fact]
    public void WhitespaceCredentialIsEncryptedNotPersistedAsPlaintext()
    {
        var protector = new VersionedSecretProtector(Ring());
        var encrypted = protector.Protect("   ", SecretPurpose.DatabasePassword, "42");
        Assert.NotNull(encrypted);
        Assert.StartsWith("v2:db_2026:", encrypted);
        Assert.Equal("   ", protector.Unprotect(encrypted, SecretPurpose.DatabasePassword, "42"));
        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect("   ", SecretPurpose.DatabasePassword, "42"));
        Assert.Equal(string.Empty, protector.Unprotect(string.Empty, SecretPurpose.DatabasePassword, "42"));
    }

    [Fact]
    public void LegacyMalformedBase64FailsAsCryptographicError()
    {
        var key = Encoding.UTF8.GetBytes("legacy-hmac-key-at-least-32-bytes-long");
        var protector = new VersionedSecretProtector(Ring(), key);
        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect("not-a-valid-base64-value!", SecretPurpose.DatabasePassword, "42"));
        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect("v2:db_2026:$bad$", SecretPurpose.DatabasePassword, "42"));
    }

    [Fact]
    public void MissingIdentityAndUnknownPurposeAreRejected()
    {
        var protector = new VersionedSecretProtector(Ring());
        Assert.Throws<ArgumentException>(() => protector.Protect("secret", SecretPurpose.DatabasePassword, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => protector.Protect("secret", (SecretPurpose)99, "id"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Protect("secret", SecretPurpose.ExtraSettings, "id"));
    }
}
