using System.Security.Cryptography;
using System.Text;
using Common.Interfaces;
using Common.Services;
using Xunit;

namespace Common.Test.Services;

public sealed class DurableDmlPayloadCiphertextReaderTests
{
    private static byte[] LegacyKey => Encoding.UTF8.GetBytes("legacy-mcp-hmac-secret-that-is-at-least-32-bytes");
    private static ISecretProtector Protector() =>
        new VersionedSecretProtector(new SecretKeyRing(
            new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DurableDmlPayload] = new SecretKeySet
                {
                    ActiveKeyId = "dml_2026",
                    Keys = new Dictionary<string, byte[]>
                    {
                        ["dml_2026"] = Enumerable.Repeat((byte)0xaa, 32).ToArray()
                    }
                }
            }), LegacyKey);

    [Fact]
    public void ExistingPendingApprovalCanStillBeReadWithLegacyKey()
    {
        var crypto = new CryptoService();
        var protectedPayload = crypto.EncryptText("{\"sql\":\"UPDATE items SET x=1\"}", LegacyKey);
        Assert.Equal("{\"sql\":\"UPDATE items SET x=1\"}",
            DurableDmlPayloadCiphertextReader.Decrypt(
                protectedPayload, "approval-123", crypto, LegacyKey));
    }

    [Fact]
    public void V2ApprovalIsBoundToRequestIdentityAndDedicatedPurpose()
    {
        var protector = Protector();
        var payload = protector.Protect("approved payload", SecretPurpose.DurableDmlPayload, "approval-123");
        Assert.Equal("approved payload",
            DurableDmlPayloadCiphertextReader.Decrypt(
                payload, "approval-123", new CryptoService(), LegacyKey, protector));
        Assert.ThrowsAny<CryptographicException>(() =>
            DurableDmlPayloadCiphertextReader.Decrypt(
                payload, "approval-other", new CryptoService(), LegacyKey, protector));
        Assert.ThrowsAny<CryptographicException>(() =>
            DurableDmlPayloadCiphertextReader.Decrypt(
                payload, "approval-123", new CryptoService(), LegacyKey));
    }

    [Fact]
    public void UnknownEnvelopeFailsClosedWithoutAttemptingLegacyFallback()
    {
        Assert.ThrowsAny<CryptographicException>(() =>
            DurableDmlPayloadCiphertextReader.Decrypt(
                "v3:dml_2026:AA==", "approval-123", new CryptoService(), LegacyKey, Protector()));
    }
}
