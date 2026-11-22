using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// ISO week-year (Thursday/January-4 rule) is distinct from Gregorian YEAR
/// at both ends of a year. Only proven PostgreSQL source spellings are accepted.
/// </summary>
public sealed class SqlCoreIsoYearCrossProviderTests
{
    private static CompiledSqlCommand Compile(string expression, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery("SELECT " + expression + " AS iso_year",
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("isoyear-cross-provider"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "YEARWEEK(", " 3) DIV 100")]
    [InlineData(SqlAgentToolType.Sqlite, "STRFTIME('%Y', DATE(", "'-3 days', 'weekday 4'")]
    public void PostgresIsoYear_PreservesWeekYearRatherThanCalendarYear(
        SqlAgentToolType target, string nativeFunction, string adjustment)
    {
        var compiled = Compile("EXTRACT(ISOYEAR FROM DATE '2018-12-31')", target);
        Assert.Contains(nativeFunction, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(adjustment, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXTRACT(ISOYEAR", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonPostgresIsoYearSource_RejectsAtParse(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT EXTRACT(ISOYEAR FROM order_date) FROM orders", source));
        Assert.Contains("ISOYEAR", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenIsoYearTargets_RemainFailClosed(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("EXTRACT(ISOYEAR FROM DATE '2018-12-31')", target));
        Assert.Contains("temporal.date_part.isoyear", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsoYearCapabilityMatrix_IsDerivedFromTargetProof()
    {
        var expected = new (SqlAgentToolType Provider, SqlCapabilityStatus Status)[]
        {
            (SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected),
            (SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected),
            (SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)
        };
        foreach (var (provider, status) in expected)
        {
            var capability = Assert.Single(
                SqlCapabilityMatrix.ForProvider(provider).Capabilities,
                item => item.Id == "temporal.date_part.iso_year");
            Assert.Equal(status, capability.Status);
        }
    }
}
