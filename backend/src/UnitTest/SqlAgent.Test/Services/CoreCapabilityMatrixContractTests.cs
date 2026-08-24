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
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
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
            Assert.Single(matrix.Capabilities, item => item.Id == "select.cte_scalar_root").Status);
        Assert.Equal(expected,
            Assert.Single(matrix.Capabilities, item => item.Id == "select.cte_definition_local").Status);
        Assert.Equal(expected,
            Assert.Single(matrix.Capabilities, item => item.Id == "dml.nested_cte_scope").Status);
    }

    [Fact]
    public void Matrix_DocumentsDefinitionLocalSetTailAndScalarTailBoundary()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres);
        var definitionLocal = Assert.Single(
            matrix.Capabilities,
            item => item.Id == "select.cte_definition_local");
        var remainingScopeGap = Assert.Single(
            matrix.Capabilities,
            item => item.Id == "select.cte_scope");

        Assert.Contains("ORDER BY/LIMIT/OFFSET", definitionLocal.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Scalar/EXISTS", remainingScopeGap.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_DocumentsPostgresStyleIntervalSourceBoundary()
    {
        var postgres = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres).Capabilities,
            item => item.Id == "expression.interval");
        var mysql = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MySQL).Capabilities,
            item => item.Id == "expression.interval");

        Assert.Equal(SqlCapabilityStatus.Supported, postgres.Status);
        Assert.Contains("declared source dialect is PostgreSQL", postgres.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SqlCapabilityStatus.Rejected, mysql.Status);
        Assert.Contains("MySQL INTERVAL expr unit", mysql.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_DocumentsPostgresDoubleColonCastSourceBoundary()
    {
        var cast = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres).Capabilities,
            item => item.Id == "expression.cast");

        Assert.Equal(SqlCapabilityStatus.Translated, cast.Status);
        Assert.Contains("Raw PostgreSQL ::", cast.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("declared source dialect is PostgreSQL", cast.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_DocumentsRowLimitSourceBoundary()
    {
        var rowLimit = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres).Capabilities,
            item => item.Id == "select.row_limit");

        Assert.Equal(SqlCapabilityStatus.Translated, rowLimit.Status);
        Assert.Contains("PostgreSQL, MySQL, and SQLite", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Raw bare OFFSET", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL and SQLite accept OFFSET only after LIMIT", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQL Server TOP", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQL Server, Oracle, and Firebird", rowLimit.Detail, StringComparison.OrdinalIgnoreCase);
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
