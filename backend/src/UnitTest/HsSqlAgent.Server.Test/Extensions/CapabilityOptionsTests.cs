using System.Text.Json;
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

    private static void AssertJsonEqual<T>(T expected, T actual)
    {
        var expectedJson = JsonSerializer.Serialize(expected);
        var actualJson = JsonSerializer.Serialize(actual);
        Assert.Equal(expectedJson, actualJson);
    }
}
