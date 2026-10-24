using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public sealed class CapabilityOptionsTests
{
    private const string ValidSecret = "test-secret-key-that-is-at-least-32-bytes";

    [Fact]
    public void LegacyDefaults_MapExactlyToModularDefaults()
    {
        var legacy = new HsSqlAgentServiceOptions();
        legacy.JwtSecretKey = ValidSecret;
        legacy.HmacSecretKey = ValidSecret;

        var legacyServices = new ServiceCollection();
        var legacyBuilder = legacyServices.AddHsSqlAgentCore(legacy);
        legacyBuilder.AddHsSqlAgentRuntime()
            .AddHsSqlAgentAdminStore()
            .AddHsSqlAgentBuiltInAuth()
            .AddHsSqlAgentMcp()
            .AddHsSqlAgentTelemetry();

        var modularServices = new ServiceCollection();
        var modularBuilder = modularServices.AddHsSqlAgentCore();
        modularBuilder.AddHsSqlAgentRuntime()
            .AddHsSqlAgentAdminStore()
            .AddHsSqlAgentBuiltInAuth(options => options.Jwt.SecretKey = ValidSecret)
            .AddHsSqlAgentMcp(options => options.HmacSecretKey = ValidSecret)
            .AddHsSqlAgentTelemetry();

        using var legacyProvider = legacyServices.BuildServiceProvider();
        using var modularProvider = modularServices.BuildServiceProvider();

        AssertJsonEqual(
            legacyProvider.GetRequiredService<HsSqlAgentRuntimeOptions>(),
            modularProvider.GetRequiredService<HsSqlAgentRuntimeOptions>());
        AssertJsonEqual(
            legacyProvider.GetRequiredService<HsSqlAgentAdminStoreOptions>(),
            modularProvider.GetRequiredService<HsSqlAgentAdminStoreOptions>());
        AssertJsonEqual(
            legacyProvider.GetRequiredService<HsSqlAgentBuiltInAuthOptions>(),
            modularProvider.GetRequiredService<HsSqlAgentBuiltInAuthOptions>());
        AssertJsonEqual(
            legacyProvider.GetRequiredService<McpOptions>(),
            modularProvider.GetRequiredService<McpOptions>());
        AssertJsonEqual(
            legacyProvider.GetRequiredService<TelemetryOptions>(),
            modularProvider.GetRequiredService<TelemetryOptions>());
    }

    [Fact]
    public void HostAuthorizationAdminApi_DoesNotAllocateUnselectedCapabilityOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
            options.AddPolicy("Host.SqlAgentAdmin", policy => policy.RequireAuthenticatedUser()));

        services.AddHsSqlAgentCore()
            .AddHsSqlAgentRuntime()
            .AddHsSqlAgentAdminStore()
            .AddHsSqlAgentHostAuthorization("Host.SqlAgentAdmin")
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<HsSqlAgentRuntimeOptions>());
        Assert.NotNull(provider.GetService<HsSqlAgentAdminStoreOptions>());
        Assert.Null(provider.GetService<HsSqlAgentBuiltInAuthOptions>());
        Assert.Null(provider.GetService<McpOptions>());
        Assert.Null(provider.GetService<TelemetryOptions>());
        Assert.Null(provider.GetService<HsSqlAgentServiceOptions>());
    }

    [Fact]
    public void EncryptionKeys_AreRegisteredOnlyWhenExplicitlyConfigured()
    {
        var noKeys = new ServiceCollection();
        noKeys.AddHsSqlAgentCore().AddHsSqlAgentRuntime();
        using (var provider = noKeys.BuildServiceProvider())
            Assert.Null(provider.GetService<ISecretProtector>());

        var configured = new ServiceCollection();
        configured.AddHsSqlAgentCore().AddHsSqlAgentRuntime(options =>
        {
            options.SecretEncryption.Enabled = true;
            options.SecretEncryption.Purposes["DatabasePassword"] = new SecretEncryptionPurposeOptions
            {
                ActiveKeyId = "db_key_1",
                Keys = new Dictionary<string, string>
                {
                    ["db_key_1"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x7a, 32).ToArray())
                }
            };
        });
        using var enabled = configured.BuildServiceProvider();
        var protector = enabled.GetRequiredService<ISecretProtector>();
        var encoded = protector.Protect("password", SecretPurpose.DatabasePassword, "id-1");
        Assert.StartsWith("v2:db_key_1:", encoded);
        Assert.Equal("password", protector.Unprotect(encoded, SecretPurpose.DatabasePassword, "id-1"));
    }

    [Fact]
    public void InvalidEncryptionConfiguration_FailsAtRegistration()
    {
        var empty = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            empty.AddHsSqlAgentCore().AddHsSqlAgentRuntime(options =>
                options.SecretEncryption.Enabled = true));

        var shortKey = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            shortKey.AddHsSqlAgentCore().AddHsSqlAgentRuntime(options =>
            {
                options.SecretEncryption.Enabled = true;
                options.SecretEncryption.Purposes["DatabasePassword"] = new SecretEncryptionPurposeOptions
                {
                    ActiveKeyId = "db_key",
                    Keys = new Dictionary<string, string> { ["db_key"] = Convert.ToBase64String(new byte[16]) }
                };
            }));
    }

    [Fact]
    public void V2DurableDmlWrites_RequireEncryptionFullFleetConfirmationAndPurposeKey()
    {
        var missingEncryption = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            missingEncryption.AddHsSqlAgentCore().AddHsSqlAgentRuntime(o =>
            {
                o.SecretEncryption.EnableV2DurableDmlWrites = true;
                o.SecretEncryption.AllNodesDualReadConfirmed = true;
            }));

        var noConfirmation = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            noConfirmation.AddHsSqlAgentCore().AddHsSqlAgentRuntime(o =>
            {
                o.SecretEncryption.Enabled = true;
                o.SecretEncryption.EnableV2DurableDmlWrites = true;
                o.SecretEncryption.Purposes["DurableDmlPayload"] = new SecretEncryptionPurposeOptions
                {
                    ActiveKeyId = "dml_key",
                    Keys = new Dictionary<string, string>
                    {
                        ["dml_key"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x61, 32).ToArray())
                    }
                };
            }));

        var missingPurpose = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            missingPurpose.AddHsSqlAgentCore().AddHsSqlAgentRuntime(o =>
            {
                o.SecretEncryption.Enabled = true;
                o.SecretEncryption.EnableV2DurableDmlWrites = true;
                o.SecretEncryption.AllNodesDualReadConfirmed = true;
                o.SecretEncryption.Purposes["DatabasePassword"] = new SecretEncryptionPurposeOptions
                {
                    ActiveKeyId = "db_key",
                    Keys = new Dictionary<string, string>
                    {
                        ["db_key"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x62, 32).ToArray())
                    }
                };
            }));
    }

    [Fact]
    public void V2DatabasePasswordWrites_RequireFleetConfirmationAndPurposeRing()
    {
        var noAcknowledgement = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            noAcknowledgement.AddHsSqlAgentCore().AddHsSqlAgentRuntime(o =>
            {
                o.SecretEncryption.Enabled = true;
                o.SecretEncryption.EnableV2DatabasePasswordWrites = true;
                o.SecretEncryption.Purposes["DatabasePassword"] = new SecretEncryptionPurposeOptions
                {
                    ActiveKeyId = "db_key",
                    Keys = new Dictionary<string, string>
                    {
                        ["db_key"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x84, 32).ToArray())
                    }
                };
            }));

        var noDbRing = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            noDbRing.AddHsSqlAgentCore().AddHsSqlAgentRuntime(o =>
            {
                o.SecretEncryption.Enabled = true;
                o.SecretEncryption.EnableV2DatabasePasswordWrites = true;
                o.SecretEncryption.AllNodesDualReadConfirmed = true;
                o.SecretEncryption.Purposes["DurableDmlPayload"] = new SecretEncryptionPurposeOptions
                {
                    ActiveKeyId = "dml_key",
                    Keys = new Dictionary<string, string>
                    {
                        ["dml_key"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x85, 32).ToArray())
                    }
                };
            }));
    }

    private static void AssertJsonEqual<T>(T expected, T actual)
    {
        var expectedJson = JsonSerializer.Serialize(expected);
        var actualJson = JsonSerializer.Serialize(actual);
        Assert.Equal(expectedJson, actualJson);
    }
}
