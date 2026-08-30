using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreUniqueKeyMetadataCapabilityMatrixTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Provider_DeclaresUniqueKeyMetadataInventory(SqlAgentToolType provider)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            x => x.Id == "provider.unique_key_metadata");

        Assert.Equal(SqlCapabilityStatus.Supported, capability.Status);
        Assert.Contains("PRIMARY", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expression", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefix", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not by itself authorize", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlUpsert_DeclaresConditionalAssuredPathWhileDefaultRemainsFailClosed()
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MySQL).Capabilities,
            x => x.Id == "dml.upsert_merge");

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains("inventories", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8.0.19", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("statement-level assurance", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sole enforced", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proposed-row alias", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deprecated VALUES(column)", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default capability remains Rejected", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("typed approval", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
