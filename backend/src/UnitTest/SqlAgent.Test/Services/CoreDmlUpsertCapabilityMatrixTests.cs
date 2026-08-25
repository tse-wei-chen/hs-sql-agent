using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlUpsertCapabilityMatrixTests
{
    [Fact]
    public void Postgres_DeclaresDeterministicConflictTargetUpsert()
    {
        var capability = Capability(SqlAgentToolType.Postgres);

        Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
        Assert.Contains("explicit conflict", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO NOTHING", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXCLUDED", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approval", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3, 23, SqlCapabilityStatus.Rejected)]
    [InlineData(3, 24, SqlCapabilityStatus.Translated)]
    [InlineData(3, 35, SqlCapabilityStatus.Translated)]
    public void Sqlite_RequiresDeclaredServerVersion324(
        int major,
        int minor,
        SqlCapabilityStatus expected)
    {
        var capability = Capability(
            SqlAgentToolType.Sqlite,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Sqlite,
                ServerVersion: new Version(major, minor)));

        Assert.Equal(expected, capability.Status);
        Assert.Contains("3.24", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sqlite_WithoutProfile_RemainsFailClosed()
    {
        var capability = Capability(SqlAgentToolType.Sqlite);

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains("fail-closed", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.24", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySql_RemainsRejectedBecauseNativeConflictTargetIsNotEquivalent()
    {
        var capability = Capability(SqlAgentToolType.MySQL);

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains("ON DUPLICATE KEY", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflict target", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void MergeProviders_RemainRejectedUntilCardinalityContractExists(
        SqlAgentToolType provider)
    {
        var capability = Capability(provider);

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains("MERGE", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cardinality", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetProfileDetail_PublishesSqliteUpsertVersionGate()
    {
        var providerCapabilities = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Sqlite);
        var capability = Assert.Single(
            providerCapabilities.Capabilities,
            x => x.Id == "provider.target_profile");

        Assert.Contains("UPSERT", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.24", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlCapability Capability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile = null) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            x => x.Id == "dml.upsert_merge");
}
