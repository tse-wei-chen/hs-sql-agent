using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// ISO week numbers and Sunday-zero weekday numbers are the only temporal
/// EXTRACT shapes proven portable between PostgreSQL and Firebird in this slice.
/// </summary>
public sealed class SqlCoreFirebirdIsoWeekWeekdayTests
{
    private static CompiledSqlCommand Compile(
        string expression, SqlAgentToolType source, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(
            "SELECT " + expression + " AS extracted FROM RDB$DATABASE",
            source, target,
            new SqlPlanValidationContext("firebird-week-weekday-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "EXTRACT(WEEK FROM DATE '2021-01-01')", "EXTRACT(WEEK")]
    [InlineData(SqlAgentToolType.Postgres, "EXTRACT(DOW FROM DATE '2021-01-03')", "EXTRACT(WEEKDAY")]
    [InlineData(SqlAgentToolType.Firebird, "EXTRACT(WEEK FROM DATE '2021-01-01')", "EXTRACT(WEEK")]
    [InlineData(SqlAgentToolType.Firebird, "EXTRACT(WEEKDAY FROM DATE '2021-01-03')", "EXTRACT(WEEKDAY")]
    public void FirebirdTarget_PreservesProvenWeekAndWeekdaySemantics(
        SqlAgentToolType source, string expression, string nativePart)
    {
        var compiled = Compile(expression, source, SqlAgentToolType.Firebird);
        Assert.Contains(nativePart, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXTRACT(DOW ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdNativeWeekday_LowersToPostgresDowWithoutChangingSundayZero()
    {
        var compiled = Compile(
            "EXTRACT(WEEKDAY FROM DATE '2021-01-03')",
            SqlAgentToolType.Firebird, SqlAgentToolType.Postgres);
        Assert.Contains("EXTRACT(DOW", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WEEKDAY", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdSource_RejectsPostgresOnlyDowSpelling()
    {
        Assert.Throws<SqlParseException>(() => Compile(
            "EXTRACT(DOW FROM DATE '2021-01-03')",
            SqlAgentToolType.Firebird, SqlAgentToolType.Firebird));
    }

    [Theory]
    [InlineData("ISODOW")]
    [InlineData("ISOYEAR")]
    public void FirebirdSource_RejectsPostgresOnlyExtractPartsBeforeTargetValidation(string part)
    {
        Assert.Throws<SqlParseException>(() => Compile(
            "EXTRACT(" + part + " FROM DATE '2021-01-03')",
            SqlAgentToolType.Firebird, SqlAgentToolType.Postgres));
    }

    [Theory]
    [InlineData("ISODOW")]
    [InlineData("ISOYEAR")]
    public void FirebirdTarget_StillRejectsUnprovenDatePartMappings(string part)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "EXTRACT(" + part + " FROM DATE '2021-01-03')",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird));
        Assert.Contains("temporal.date_part.", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
