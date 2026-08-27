using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCapabilityMatrixVersionContractTests
{
    [Fact]
    public void MatrixVersion_IsCurrentCapabilityContract()
    {
        Assert.Equal("2026-08-27.45", SqlCapabilityMatrix.Version);
    }
}
