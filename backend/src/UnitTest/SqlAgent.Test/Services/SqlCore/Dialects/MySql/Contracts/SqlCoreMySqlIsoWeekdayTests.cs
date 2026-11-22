using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL ISODOW is Monday=1..Sunday=7; native MySQL WEEKDAY is
/// Monday=0..Sunday=6. Only proven source grammar may enter this lowering.
/// </summary>
public sealed class SqlCoreMySqlIsoWeekdayTests
{
    private const string Query =
        "SELECT EXTRACT(ISODOW FROM event_date) AS iso_day FROM iso_weekday_cases";

    [Fact]
    public void PostgresIsoWeekdayToMySql_UsesNativeMondayZeroWeekdayPlusOne()
    {
        var compiled = SqlCoreFacade.CompileQuery(
            Query, SqlAgentToolType.Postgres, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-isodow-compile"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("WEEKDAY(", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+ 1", compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTRACT(ISODOW", compiled.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlSourceCannotBorrowPostgresOnlyIsoDowSpelling()
    {
        Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Query, SqlAgentToolType.MySQL));
    }

    [Fact]
    public void UnprovenIsoYearRemainsCapabilityDeniedOnMySqlTarget()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT EXTRACT(ISOYEAR FROM event_date) FROM iso_weekday_cases",
                SqlAgentToolType.Postgres, SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("mysql-isoyear-negative"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("isoyear", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    public void IsoWeekdayCapabilityMatchesActuallyProvenTargets(
        SqlAgentToolType provider, SqlCapabilityStatus expected)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.date_part.iso_weekday");
        Assert.Equal(expected, capability.Status);
    }
}
