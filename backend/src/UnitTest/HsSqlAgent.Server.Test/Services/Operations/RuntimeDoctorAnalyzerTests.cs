using System.Text.Json;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class RuntimeDoctorAnalyzerTests
{
    [Theory]
    [InlineData("SecretProtection:DatabasePasswords:Keys:db-1", "YourDbEncryptionKeyHere-Base64Encoded32Bytes!")]
    [InlineData("SecretProtection:DmlPayloads:Keys:dml-1", "YourDmlEncryptionKeyHere-Base64Encoded32Bytes!")]
    [InlineData("SecretProtection:DmlFingerprintKey", "YourDmlFingerprintKeyHere-Base64Encoded32Bytes!")]
    public void Analyze_ReadableExamplePlaceholdersProduceExplicitError(string path, string placeholder)
    {
        var values = BuildHealthyBase();
        values[path] = placeholder;
        var result = RuntimeDoctorAnalyzer.Analyze(BuildConfiguration(values), "Development", 1, 1, 1);
        var check = Assert.Single(result.Checks, item => item.Id == "security.secret-protection");
        Assert.Equal("Error", check.Status);
        Assert.Contains("placeholder", check.Detail);
        Assert.DoesNotContain(placeholder, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SecretProtection:DatabasePasswords:ActiveKeyId", "unavailable-key-id")]
    [InlineData("SecretProtection:DmlPayloads:ActiveKeyId", "bad:id")]
    [InlineData("SecretProtection:DatabasePasswords:Keys:db-1", "")]
    [InlineData("SecretProtection:DmlPayloads:Keys:dml-1", "not-base64!")]
    [InlineData("SecretProtection:DmlFingerprintKey", "YWJj")]
    [InlineData("SecretProtection:DatabasePasswords:Keys:retired", "broken-format-secret-marker")]
    public void Analyze_InvalidProtectionKeysFailWithoutLeakingValues(string path, string value)
    {
        var values = BuildHealthyBase();
        values[path] = value;
        var result = RuntimeDoctorAnalyzer.Analyze(BuildConfiguration(values), "Development", 1, 1, 1);
        Assert.Equal("Error", Assert.Single(result.Checks, check => check.Id == "security.secret-protection").Status);
        Assert.Equal("Error", result.OverallStatus);
        if (value.Length > 5) Assert.DoesNotContain(value, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development", "Warning", false)]
    [InlineData("Production", "Error", false)]
    [InlineData("Production", "Error", true)]
    public void Analyze_PublicSampleKeysAreFlaggedIncludingRetainedKeys(string environment, string expected, bool retained)
    {
        var values = BuildHealthyBase();
        var sample = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sample-db-password-key".PadRight(32, '!')));
        values["SecretProtection:DatabasePasswords:Keys:" + (retained ? "retired" : "db-1")] = sample;
        var result = RuntimeDoctorAnalyzer.Analyze(BuildConfiguration(values), environment, 1, 1, 1);
        Assert.Equal(expected, Assert.Single(result.Checks, check => check.Id == "security.secret-protection").Status);
        Assert.Equal(expected, result.OverallStatus);
        Assert.DoesNotContain(sample, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SecretProtection:DmlPayloads:Keys:dml-1")]
    [InlineData("SecretProtection:DmlFingerprintKey")]
    [InlineData("McpKeySettings:HmacSecretKey")]
    [InlineData("JwtSettings:SecretKey")]
    public void Analyze_ReusedKeysFailWithoutExposingThem(string duplicatePath)
    {
        var values = BuildHealthyBase();
        var key = values["SecretProtection:DatabasePasswords:Keys:db-1"]!;
        values[duplicatePath] = key;
        var result = RuntimeDoctorAnalyzer.Analyze(BuildConfiguration(values), "Production", 1, 1, 1);
        Assert.Equal("Error", Assert.Single(result.Checks, check => check.Id == "security.secret-protection").Status);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_HealthySingleNodeConfiguration_IsHealthyAndDoesNotExposeSecrets()
    {
        const string hmac = "0123456789abcdef0123456789abcdef";
        const string jwt = "fedcba9876543210fedcba9876543210";
        var values = BuildHealthyBase();
        values["Mcp:PublicEndpoint"] = "http://localhost:8080/mcp";
        values["AdminDatabase:Provider"] = "Sqlite";
        values["AdminDatabase:ConnectionString"] = "Data Source=hsqlagent.db";
        values["McpKeySettings:HmacSecretKey"] = hmac;
        values["JwtSettings:SecretKey"] = jwt;

        var result = RuntimeDoctorAnalyzer.Analyze(
            BuildConfiguration(values),
            "Development",
            1,
            1,
            1);

        Assert.Equal("Healthy", result.OverallStatus);
        Assert.Equal("SingleNode", result.DeploymentMode);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(hmac, json, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, json, StringComparison.Ordinal);
        foreach (var value in values.Where(pair => pair.Key.StartsWith("SecretProtection:") && !pair.Key.EndsWith("ActiveKeyId")).Select(pair => pair.Value!))
            Assert.DoesNotContain(value, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ExampleSecrets_FailReadiness()
    {
        var values = BuildHealthyBase();
        values["McpKeySettings:HmacSecretKey"] = "YourMcpHmacSecretKeyHere-AtLeast32Bytes!";
        values["JwtSettings:SecretKey"] = "YourSuperSecretKeyHere-AtLeast32Bytes!";

        var result = RuntimeDoctorAnalyzer.Analyze(
            BuildConfiguration(values),
            "Production",
            1,
            1,
            1);

        Assert.Equal("Error", result.OverallStatus);
        Assert.Contains(result.Checks, check => check.Id == "security.mcp-hmac" && check.Status == "Error");
        Assert.Contains(result.Checks, check => check.Id == "security.jwt" && check.Status == "Error");
    }

    [Fact]
    public void Analyze_MixedCoordinationAndMissingRedisConnection_AreVisible()
    {
        var values = BuildHealthyBase();
        values["CacheConfig:Provider"] = "Redis";
        values["CacheConfig:ConnectionString"] = "";
        values["RateLimiter:Provider"] = "Memory";
        values["SecurityPolicySync:Provider"] = "Redis";
        values["SecurityPolicySync:ConnectionString"] = "redis:6379";
        values["OutboundDeliverySync:Provider"] = "Memory";
        values["SqlConcurrency:Provider"] = "Memory";

        var result = RuntimeDoctorAnalyzer.Analyze(
            BuildConfiguration(values),
            "Production",
            1,
            1,
            1);

        Assert.Equal("Mixed", result.DeploymentMode);
        Assert.Equal("Error", result.OverallStatus);
        Assert.Contains(result.Checks, check => check.Id == "coordination.cacheconfig" && check.Status == "Error");
        Assert.Contains(result.Checks, check => check.Id == "coordination.mode" && check.Status == "Warning");
    }

    [Fact]
    public void Analyze_WebhookApprovalRequiresCompleteSignedConfiguration()
    {
        var values = BuildHealthyBase();
        values["DmlApproval:Provider"] = "Webhook";
        values["DmlApproval:Webhook:Endpoint"] = "https://approval.example.com/request";
        values["DmlApproval:Webhook:CallbackUrl"] = "https://sql.example.com/callback";
        values["DmlApproval:Webhook:SigningSecret"] = "short";

        var result = RuntimeDoctorAnalyzer.Analyze(
            BuildConfiguration(values),
            "Production",
            1,
            1,
            1);

        var check = Assert.Single(result.Checks, check => check.Id == "approval.provider");
        Assert.Equal("Error", check.Status);
        Assert.Contains("SigningSecret", check.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("short", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_NoTargetOrActiveKey_ProducesActionableReadinessWarnings()
    {
        var result = RuntimeDoctorAnalyzer.Analyze(
            BuildConfiguration(BuildHealthyBase()),
            "Development",
            0,
            0,
            0);

        Assert.Equal("Warning", result.OverallStatus);
        Assert.Contains(result.Checks, check => check.Id == "readiness.database" && check.Status == "Warning");
        Assert.Contains(result.Checks, check => check.Id == "readiness.mcp-key" && check.Status == "Warning");
    }

    private static Dictionary<string, string?> BuildHealthyBase() => new()
    {
        ["SecretProtection:DatabasePasswords:ActiveKeyId"] = "db-1",
        ["SecretProtection:DatabasePasswords:Keys:db-1"] = Common.Services.TestSecrets.Create().DatabasePasswords.Keys["db-1"],
        ["SecretProtection:DmlPayloads:ActiveKeyId"] = "dml-1",
        ["SecretProtection:DmlPayloads:Keys:dml-1"] = Common.Services.TestSecrets.Create().DmlPayloads.Keys["dml-1"],
        ["SecretProtection:DmlFingerprintKey"] = Common.Services.TestSecrets.Create().DmlFingerprintKey,
        ["McpKeySettings:HmacSecretKey"] = "0123456789abcdef0123456789abcdef",
        ["JwtSettings:SecretKey"] = "fedcba9876543210fedcba9876543210",
        ["Mcp:PublicEndpoint"] = "https://sql.example.com/mcp",
        ["AdminDatabase:Provider"] = "Postgres",
        ["AdminDatabase:ConnectionString"] = "Host=db;Database=admin",
        ["EnterpriseIdentity:DataProtectionKeyPath"] = "/keys",
        ["EnterpriseIdentity:OidcEnabled"] = "false",
        ["DmlApproval:Provider"] = "McpElicitation",
        ["CacheConfig:Provider"] = "Memory",
        ["RateLimiter:Provider"] = "Memory",
        ["SecurityPolicySync:Provider"] = "Memory",
        ["OutboundDeliverySync:Provider"] = "Memory",
        ["SqlConcurrency:Provider"] = "Memory"
    };

    private static IConfiguration BuildConfiguration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
