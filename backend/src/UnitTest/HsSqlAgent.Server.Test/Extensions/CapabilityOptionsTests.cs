using System.Security.Cryptography;
using System.Text.Json;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public sealed class CapabilityOptionsTests
{
    private static void ConfigureV3(HsSqlAgentRuntimeOptions options)
    {
        options.SecretEncryption.Purposes["DatabasePassword"] = new SecretEncryptionPurposeOptions
        {
            ActiveKeyId = "db_k1",
            Keys = new Dictionary<string, string> {
                ["db_k1"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x52,32).ToArray())
            }
        };
        options.SecretEncryption.Purposes["DurableDmlPayload"] = new SecretEncryptionPurposeOptions
        {
            ActiveKeyId = "dml_k1",
            Keys = new Dictionary<string, string> {
                ["dml_k1"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x53,32).ToArray())
            }
        };
    }

    [Fact]
    public void V3RejectsMissingKeyRingsAtRegistration()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddHsSqlAgentCore().AddHsSqlAgentRuntime());
    }

    [Theory]
    [InlineData("DatabasePassword")]
    [InlineData("DurableDmlPayload")]
    public void V3RequiresBothPurposes(string only)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddHsSqlAgentCore().AddHsSqlAgentRuntime(options =>
            {
                ConfigureV3(options);
                options.SecretEncryption.Purposes.Remove(
                    only == "DatabasePassword" ? "DurableDmlPayload" : "DatabasePassword");
            }));
    }

    [Fact]
    public void V3RejectsShortOrReusedKeys()
    {
        var shortKey = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            shortKey.AddHsSqlAgentCore().AddHsSqlAgentRuntime(options =>
            {
                ConfigureV3(options);
                options.SecretEncryption.Purposes["DatabasePassword"].Keys["db_k1"] =
                    Convert.ToBase64String(new byte[16]);
            }));

        var duplicateKey = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            duplicateKey.AddHsSqlAgentCore().AddHsSqlAgentRuntime(options =>
            {
                ConfigureV3(options);
                options.SecretEncryption.Purposes["DurableDmlPayload"].Keys["dml_k1"] =
                    options.SecretEncryption.Purposes["DatabasePassword"].Keys["db_k1"];
            }));
    }

    [Fact]
    public void V3RegistrationProducesBothAuthenticatedWritersAndRejectsLegacy()
    {
        var services = new ServiceCollection();
        services.AddHsSqlAgentCore().AddHsSqlAgentRuntime(ConfigureV3);
        using var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var db = protector.Protect("private", SecretPurpose.DatabasePassword, "42");
        var dml = protector.Protect("approved", SecretPurpose.DurableDmlPayload, "approval-1");
        Assert.StartsWith("v2:db_k1:", db);
        Assert.StartsWith("v2:dml_k1:", dml);
        Assert.Equal("private", protector.Unprotect(db, SecretPurpose.DatabasePassword, "42"));
        Assert.Equal("approved", protector.Unprotect(dml, SecretPurpose.DurableDmlPayload, "approval-1"));
        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect("old-base64-password", SecretPurpose.DatabasePassword, "42"));
        var rollout = provider.GetRequiredService<ISecretWriteRolloutPolicy>();
        Assert.True(rollout.EnableV2DatabasePasswordWrites);
        Assert.True(rollout.EnableV2DurableDmlWrites);
    }
}
