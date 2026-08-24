using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CorePostgresLimitAllCapabilityContractTests
{
    [Fact]
    public void Matrix_DocumentsPostgresLimitAllSourceBoundary()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres);
        var rowLimit = Assert.Single(
            matrix.Capabilities,
            item => item.Id == "select.row_limit");

        Assert.Equal(SqlCapabilityStatus.Translated, rowLimit.Status);
        Assert.Contains("PostgreSQL LIMIT ALL", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no row-count limit", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT ALL OFFSET n", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL and SQLite reject LIMIT ALL", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT and FETCH clauses remain mutually exclusive", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
