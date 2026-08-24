using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreCapabilityMatrixContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Matrix_AdvertisesInsertSelectAndOrdinalOrdering(SqlAgentToolType provider)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);

        Assert.Equal(SqlCapabilityStatus.Translated,
            Assert.Single(matrix.Capabilities, item => item.Id == "dml.insert_select").Status);
        Assert.Equal(SqlCapabilityStatus.Translated,
            Assert.Single(matrix.Capabilities, item => item.Id == "ordering.ordinal").Status);
        Assert.Contains("INSERT ... SELECT",
            Assert.Single(matrix.Capabilities, item => item.Id == "dml.advanced").Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected)]
    public void Matrix_TracksNestedCteProviderBoundary(
        SqlAgentToolType provider,
        SqlCapabilityStatus expected)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);

        Assert.Equal(expected,
            Assert.Single(matrix.Capabilities, item => item.Id == "select.cte_derived").Status);
        Assert.Equal(expected,
            Assert.Single(matrix.Capabilities, item => item.Id == "select.cte_set_branch").Status);
        Assert.Equal(expected,
            Assert.Single(matrix.Capabilities, item => item.Id == "dml.nested_cte_scope").Status);
    }

    [Fact]
    public void Matrix_DocumentsVersionDependentSqlServerRegexBoundary()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer);
        var regex = Assert.Single(matrix.Capabilities, item => item.Id == "regex.match");

        Assert.Equal(SqlCapabilityStatus.Rejected, regex.Status);
        Assert.Contains("compatibility level 170", regex.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
