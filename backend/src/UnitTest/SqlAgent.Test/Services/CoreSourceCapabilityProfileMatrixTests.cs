using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSourceCapabilityProfileMatrixTests
{
    [Fact]
    public void Matrix_DocumentsSeparateSourceRuntimeProfile()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MySQL);
        var sourceProfile = Assert.Single(
            matrix.Capabilities,
            item => item.Id == "provider.source_profile");
        var concat = Assert.Single(
            matrix.Capabilities,
            item => item.Id == "expression.concat");

        Assert.Equal(SqlCapabilityStatus.Supported, sourceProfile.Status);
        Assert.Contains("separate", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PIPES_AS_CONCAT", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ANSI_QUOTES", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("double-quoted identifiers", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO_BACKSLASH_ESCAPES", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ANSI does not imply", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIKE", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PIPES_AS_CONCAT", concat.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target profile alone", concat.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
