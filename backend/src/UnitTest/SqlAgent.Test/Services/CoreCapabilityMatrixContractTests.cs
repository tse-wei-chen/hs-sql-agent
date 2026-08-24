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

    [Fact]
    public void Matrix_DocumentsVersionDependentSqlServerRegexBoundary()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer);
        var regex = Assert.Single(matrix.Capabilities, item => item.Id == "regex.match");

        Assert.Equal(SqlCapabilityStatus.Rejected, regex.Status);
        Assert.Contains("compatibility level 170", regex.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
