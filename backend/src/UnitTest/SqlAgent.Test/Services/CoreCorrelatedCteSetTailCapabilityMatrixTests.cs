using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCorrelatedCteSetTailCapabilityMatrixTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Matrix_PublishesCorrelatedScalarSetTailSubset(SqlAgentToolType provider)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);
        var scalar = Assert.Single(matrix.Capabilities, item => item.Id == "select.cte_scalar_root");
        var scope = Assert.Single(matrix.Capabilities, item => item.Id == "select.cte_scope");
        var dml = Assert.Single(matrix.Capabilities, item => item.Id == "dml.nested_cte_scope");

        Assert.Equal("2026-08-26.39", matrix.MatrixVersion);
        Assert.Equal(SqlCapabilityStatus.Translated, scalar.Status);
        Assert.Contains("combined output name", scalar.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output ordinal", scalar.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correlated outer", scalar.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(SqlCapabilityStatus.Rejected, scope.Status);
        Assert.Contains("Richer set-result ORDER BY", scope.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", scope.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(SqlCapabilityStatus.Translated, dml.Status);
        Assert.Contains("scope-preserving direct", dml.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output ordinal", dml.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
