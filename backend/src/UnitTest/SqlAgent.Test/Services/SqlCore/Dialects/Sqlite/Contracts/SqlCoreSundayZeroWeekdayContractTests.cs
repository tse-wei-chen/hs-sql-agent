using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL DOW and native Firebird WEEKDAY both count Sunday=0..Saturday=6.
/// MySQL DAYOFWEEK returns Sunday=1..Saturday=7; SQLite STRFTIME('%w') is text.
/// </summary>
public sealed class SqlCoreSundayZeroWeekdayContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "DOW", SqlAgentToolType.MySQL, "DAYOFWEEK(", " - 1")]
    [InlineData(SqlAgentToolType.Postgres, "DOW", SqlAgentToolType.Sqlite, "STRFTIME('%w',", "AS INTEGER")]
    [InlineData(SqlAgentToolType.Firebird, "WEEKDAY", SqlAgentToolType.MySQL, "DAYOFWEEK(", " - 1")]
    public void SundayZeroWeekday_UsesNativeTargetNumbering(
        SqlAgentToolType source, string sourcePart, SqlAgentToolType target,
        string nativeFunction, string conversion)
    {
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT EXTRACT(" + sourcePart + " FROM DATE '2021-01-03') AS day_no",
            source, target,
            new SqlPlanValidationContext("sunday-zero-weekday-contract"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

        Assert.Contains(nativeFunction, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(conversion, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXTRACT(DOW", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void NonPostgresDOWSource_DoesNotBorrowPostgresGrammar(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT EXTRACT(DOW FROM order_date) FROM orders", source));
        Assert.Contains("DOW", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    public void SundayZeroWeekdayCapability_ExposesCompilerContract(
        SqlAgentToolType provider, SqlCapabilityStatus expected)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.date_part.sunday_zero_weekday");
        Assert.Equal(expected, capability.Status);
    }
}
