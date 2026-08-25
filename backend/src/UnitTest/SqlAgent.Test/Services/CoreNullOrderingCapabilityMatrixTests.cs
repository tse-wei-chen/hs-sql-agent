using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreNullOrderingCapabilityMatrixTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Matrix_DocumentsDirectColumnInverseNullOrderingSubset(SqlAgentToolType provider)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "ordering.nulls");

        Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
        Assert.Contains("direct row-source column", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISTINCT", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set-operation", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("projection alias", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("computed", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
