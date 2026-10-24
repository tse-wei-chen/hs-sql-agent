using System.Security.Cryptography;
using Common.Services;
using Xunit;

namespace Admin.Test.Services;

public sealed class CryptoServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("密碼 🔑")]
    public void RoundTripPreservesExactText(string value)
    {
        var crypto = new CryptoService(TestSecrets.Create());
        var encrypted = crypto.EncryptText(value, "db-password:record-1");
        Assert.StartsWith("v2:db-1:", encrypted);
        Assert.NotEqual(value, encrypted);
        Assert.Equal(value, crypto.DecryptText(encrypted, "db-password:record-1"));
    }

    [Fact]
    public void RotationSupportsRestartAndIndependentNodes()
    {
        var settings = TestSecrets.Create();
        var old = new CryptoService(settings).EncryptText("password", "db-password:record-1");
        settings.DatabasePasswords.Keys["db-2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        settings.DatabasePasswords.ActiveKeyId = "db-2";
        var node = new CryptoService(settings);
        Assert.Equal("password", node.DecryptText(old, "db-password:record-1"));
        var current = node.EncryptText("password", "db-password:record-1");
        Assert.StartsWith("v2:db-2:", current);
        Assert.Equal("password", new CryptoService(settings).DecryptText(current, "db-password:record-1"));
        settings.DatabasePasswords.Keys.Remove("db-1");
        Assert.Throws<CryptographicException>(() => new CryptoService(settings).DecryptText(old, "db-password:record-1"));
    }

    [Fact]
    public void WrongKeyPurposeRecordAndTamperingAreRejected()
    {
        var settings = TestSecrets.Create();
        var crypto = new CryptoService(settings);
        var encrypted = crypto.EncryptText("secret", "db-password:record-1")!;
        Assert.ThrowsAny<CryptographicException>(() => crypto.DecryptText(encrypted, "db-password:record-2"));
        Assert.ThrowsAny<CryptographicException>(() => crypto.DecryptText(encrypted, "dml-payload:record-1"));
        var parts = encrypted.Split(':');
        var bytes = Convert.FromBase64String(parts[2]);
        foreach (var index in new[] { 0, 12, 28 })
        {
            bytes[index] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => crypto.DecryptText($"v2:db-1:{Convert.ToBase64String(bytes)}", "db-password:record-1"));
            bytes[index] ^= 1;
        }
        settings.DatabasePasswords.Keys["db-1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => new CryptoService(settings).DecryptText(encrypted, "db-password:record-1"));
    }

    [Theory]
    [InlineData("bGVnYWN5")]
    [InlineData("v3:db-1:AAAA")]
    [InlineData("v2:missing:AAAA")]
    [InlineData("v2:db-1:AAAA")]
    [InlineData("v2:db-1:invalid!")]
    public void LegacyUnknownAndMalformedEnvelopesFailClosed(string encrypted) =>
        Assert.ThrowsAny<CryptographicException>(() => new CryptoService(TestSecrets.Create()).DecryptText(encrypted, "db-password:record-1"));

    [Fact]
    public void NoncesAreFresh()
    {
        var crypto = new CryptoService(TestSecrets.Create());
        var nonces = Enumerable.Range(0, 256).Select(_ =>
            Convert.ToHexString(Convert.FromBase64String(crypto.EncryptText("same", "db-password:record")!.Split(':')[2])[..12]));
        Assert.Equal(256, nonces.Distinct().Count());
    }

    [Fact]
    public void MissingShortAndReusedKeysFailAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new CryptoService(new()));
        var settings = TestSecrets.Create();
        settings.DatabasePasswords.Keys["db-1"] = Convert.ToBase64String(new byte[16]);
        Assert.Throws<InvalidOperationException>(() => new CryptoService(settings));
        settings = TestSecrets.Create();
        settings.DmlPayloads.Keys["dml-1"] = settings.DatabasePasswords.Keys["db-1"];
        Assert.Throws<InvalidOperationException>(() => new CryptoService(settings));
    }

    [Theory]
    [InlineData("{\"Password\":\"secret\"}")]
    [InlineData("{\"Encrypt\":\"true\"}")]
    [InlineData("{\"Encrypt\":true,\"Encrypt\":false}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public void ExtraSettingsRejectSecretsAndUnexpectedShapes(string settings) =>
        Assert.Throws<ArgumentException>(() => ConnectionExtraSettings.Validate(settings));

    [Fact]
    public void ExtraSettingsAcceptOnlyKnownBooleanOptions() =>
        ConnectionExtraSettings.Validate("{\"Encrypt\":true,\"TrustServerCertificate\":false}");
}
