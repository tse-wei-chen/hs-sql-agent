using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL DOY is 1..366; Firebird YEARDAY is 0..365.
/// Both semantic tags must remain distinct when targeting MySQL or SQLite.
/// </summary>
public sealed class SqlCoreDayOfYearCrossProviderTests
{
    [Theory]
    [InlineData("DOY", SqlAgentToolType.Postgres, SqlAgentToolType.MySQL, "DAYOFYEAR(")]
    [InlineData("DOY", SqlAgentToolType.Postgres, SqlAgentToolType.Sqlite, "STRFTIME('%j',")]
    [InlineData("YEARDAY", SqlAgentToolType.Firebird, SqlAgentToolType.MySQL, "DAYOFYEAR(")]
    [InlineData("YEARDAY", SqlAgentToolType.Firebird, SqlAgentToolType.Sqlite, "STRFTIME('%j',")]
    public void DayOfYear_LosslesslyLowersBothNumberingConventions(
        string part, SqlAgentToolType source, SqlAgentToolType target, string native)
    {
        var sql = "SELECT EXTRACT(" + part + " FROM DATE '2024-02-29') " +
                  "AS day_no FROM RDB$DATABASE";
        var command = SqlCoreFacade.CompileQuery(sql, source, target,
            new SqlPlanValidationContext("portable-day-of-year-contract"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
        Assert.Contains(native, command.Sql, StringComparison.OrdinalIgnoreCase);
        if (part == "YEARDAY")
            Assert.Contains(" - 1", command.Sql, StringComparison.Ordinal);
        else
            Assert.DoesNotContain(" - 1", command.Sql, StringComparison.Ordinal);
        if (target == SqlAgentToolType.Sqlite)
            Assert.Contains("AS INTEGER", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void NonPostgresDoySourceSyntax_RemainsRejectedAtParse(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT EXTRACT(DOY FROM event_date) FROM events", source));
        Assert.Contains("DOY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void UnprovenEpoch_RemainsTargetDenied(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT EXTRACT(EPOCH FROM DATE '2024-02-29')",
                SqlAgentToolType.Postgres, target,
                new SqlPlanValidationContext("portable-doy-isoyear-negative"),
                new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken));
        Assert.Contains("temporal.date_part.epoch", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
