using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCapabilityMatrixVersionContractTests
{
    [Fact]
    public void MatrixVersion_IsCurrentCapabilityContract()
    {
        Assert.Equal("2026-09-02.77", SqlCapabilityMatrix.Version);
    }

    [Fact]
    public void NativeFunctionIdentifierCapabilities_ReflectProviderGrammar()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var matrix = SqlCapabilityMatrix.ForProvider(provider);
            var quoted = Assert.Single(
                matrix.Capabilities,
                capability => capability.Id == "function.quoted_identifier");
            var qualified = Assert.Single(
                matrix.Capabilities,
                capability => capability.Id == "function.qualified");

            Assert.Equal(SqlCapabilityStatus.Supported, quoted.Status);
            Assert.Equal(
                provider == SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                qualified.Status);
        }
    }

    [Fact]
    public void MatrixDetails_DoNotReferenceRemovedSqlKataBackend()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var matrix = SqlCapabilityMatrix.ForProvider(provider);
            foreach (var capability in matrix.Capabilities)
            {
                Assert.DoesNotContain(
                    "SqlKata",
                    capability.Detail,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
