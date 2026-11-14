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
                new Version(major, minor)));

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
        Assert.Contains("inventories", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sole enforced", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("statement-level assurance", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Firebird_DefaultMatrixRemainsRejectedButPublishesPrimaryKeyAssurancePath()
    {
        var capability = Capability(SqlAgentToolType.Firebird);

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains("UPDATE OR INSERT", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MATCHING", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("primary key", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assurance", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServer_DefaultMatrixRejectsButPublishesAssuredSingleRowMergePath()
    {
        var capability = Capability(SqlAgentToolType.MsSqlServer);
        var merge = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer).Capabilities,
            x => x.Id == "dml.merge.single_row");

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, merge.Status);
        Assert.Contains("single-row MERGE", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DmlConflictTargetAssurance", merge.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cardinality", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oracle_MergeRemainsRejectedUntilOracleGrammarContractExists()
    {
        var capability = Capability(SqlAgentToolType.Oracle);
        var merge = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Oracle).Capabilities,
            x => x.Id == "dml.merge.single_row");

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, merge.Status);
        Assert.Contains("Oracle", merge.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MERGE", merge.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", merge.Detail, StringComparison.OrdinalIgnoreCase);
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
