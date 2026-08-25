using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreLikeEscapeCapabilityMatrixTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Matrix_AdvertisesPortableExplicitLikeEscape(SqlAgentToolType provider)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "expression.like_escape");

        Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
        Assert.Contains("single-character", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all target providers", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parameterized", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider-default", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
