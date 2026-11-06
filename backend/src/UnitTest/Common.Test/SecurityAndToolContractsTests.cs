using System.Security.Cryptography;
using Common.Models;
using Common.Services;
using Xunit;

namespace Common.Test;

public class SecurityAndToolContractsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{}")]
    [InlineData("{\"Encrypt\":true}")]
    [InlineData("{\"Encrypt\":false,\"TrustServerCertificate\":true}")]
    public void ExtraSettings_AcceptsOnlyWhitelistedBooleanOptions(string? json)
    {
        ConnectionExtraSettings.Validate(json);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"Encrypt\":\"false\"}")]
    [InlineData("{\"encrypt\":true}")]
    [InlineData("{\"Password\":\"secret\"}")]
    [InlineData("{\"Encrypt\":true,\"Encrypt\":false}")]
    [InlineData("{\"TrustServerCertificate\":null}")]
    public void ExtraSettings_RejectsUnknownNonBooleanOrDuplicateOptions(string json)
    {
        Assert.Throws<ArgumentException>(() => ConnectionExtraSettings.Validate(json));
    }

    [Fact]
    public void BuiltInToolCatalog_IsSixCanonicalToolsWithExactlyFourSafeDefaults()
    {
        Assert.Equal(6, McpBuiltInTools.Catalog.Count);
        Assert.Equal(6, McpBuiltInTools.Names.Count);
        Assert.Equal(4, McpBuiltInTools.DefaultNames.Count);
        Assert.All(McpBuiltInTools.Catalog, tool => Assert.True(tool.IsBuiltIn));
        Assert.Contains(McpBuiltInTools.GetSchemas, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.Contains(McpBuiltInTools.GetTables, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.Contains(McpBuiltInTools.GetColumns, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.Contains(McpBuiltInTools.ExecuteQuerySql, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.DoesNotContain(McpBuiltInTools.ExecuteDmlSql, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.DoesNotContain(McpBuiltInTools.ExecuteDdlSql, McpBuiltInTools.DefaultNames.AsEnumerable());
        // Exercise the FrozenSet lookup comparer, not xUnit's default enumerable string comparison.
        Assert.True(McpBuiltInTools.Names.Contains("GET_SCHEMAS"), "Tool lookup must be case-insensitive.");
    }

    [Fact]
    public void SecretEnvelope_RoundTripsUnicodeButIsBoundToTheRequestedContext()
    {
        var crypto = CreateCrypto();
        const string context = "db-password:connection-17";
        const string value = "資料庫密碼 🔒";
        var cipher = Assert.IsType<string>(crypto.EncryptText(value, context));
        Assert.StartsWith("v2:active:", cipher);
        Assert.NotEqual(value, cipher);
        Assert.Equal(value, crypto.DecryptText(cipher, context));
        Assert.Throws<CryptographicException>(
            () => crypto.DecryptText(cipher, "db-password:connection-18"));
        Assert.Throws<CryptographicException>(
            () => crypto.DecryptText(cipher, "dml-payload:connection-17"));
    }

    [Fact]
    public void SecretEnvelope_RejectsTamperedAuthenticatedCiphertext()
    {
        var crypto = CreateCrypto();
        var cipher = Assert.IsType<string>(crypto.EncryptText("payload", "dml-payload:request-1"));
        var parts = cipher.Split(':');
        var bytes = Convert.FromBase64String(parts[2]);
        bytes[^1] ^= 1;
        var tampered = $"{parts[0]}:{parts[1]}:{Convert.ToBase64String(bytes)}";
        Assert.Throws<CryptographicException>(
            () => crypto.DecryptText(tampered, "dml-payload:request-1"));
    }

    [Fact]
    public void SecretEnvelope_RejectsMissingKeyIdAndUnsupportedVersion()
    {
        var crypto = CreateCrypto();
        var cipher = Assert.IsType<string>(crypto.EncryptText("payload", "db-password:connection-17"));
        Assert.Throws<CryptographicException>(
            () => crypto.DecryptText(cipher.Replace(":active:", ":removed:"), "db-password:connection-17"));
        Assert.Throws<CryptographicException>(
            () => crypto.DecryptText(cipher.Replace("v2:", "v1:"), "db-password:connection-17"));
    }

    [Fact]
    public void EncryptionKeys_MustBeIndependentAcrossPurposesAndFingerprint()
    {
        var key = RandomKey();
        var settings = Settings();
        settings.DmlPayloads.Keys["active"] = key;
        settings.DatabasePasswords.Keys["active"] = key;
        Assert.Throws<InvalidOperationException>(() => new CryptoService(settings));

        settings = Settings();
        settings.DmlFingerprintKey = settings.DatabasePasswords.Keys["active"];
        Assert.Throws<InvalidOperationException>(() => new CryptoService(settings));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("")]
    [InlineData("AQID")]
    public void EncryptionKeys_RejectNon32ByteOrNonBase64Material(string value)
    {
        Assert.Throws<InvalidOperationException>(() => CryptoService.DecodeKey(value));
    }

    [Fact]
    public void EncryptionKeys_RejectUnsafeKeyIdentifiers()
    {
        var settings = Settings();
        settings.DatabasePasswords.Keys.Add("unsafe:id", RandomKey());
        Assert.Throws<InvalidOperationException>(() => new CryptoService(settings));
    }

    private static CryptoService CreateCrypto() => new(Settings());

    private static SecretProtectionSettings Settings() => new()
    {
        DatabasePasswords = new SecretKeyRing
        {
            ActiveKeyId = "active",
            Keys = new Dictionary<string, string> { ["active"] = RandomKey() },
        },
        DmlPayloads = new SecretKeyRing
        {
            ActiveKeyId = "active",
            Keys = new Dictionary<string, string> { ["active"] = RandomKey() },
        },
        DmlFingerprintKey = RandomKey(),
    };

    private static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
