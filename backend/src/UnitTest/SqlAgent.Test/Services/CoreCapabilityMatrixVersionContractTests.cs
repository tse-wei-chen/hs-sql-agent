using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCapabilityMatrixVersionContractTests
{
    [Fact]
    public void MatrixVersion_IsCurrentCapabilityContract()
    {
        Assert.Equal("2026-08-31.60", SqlCapabilityMatrix.Version);
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
