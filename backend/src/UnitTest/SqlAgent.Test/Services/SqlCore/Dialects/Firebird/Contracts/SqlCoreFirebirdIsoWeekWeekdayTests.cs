using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Source-specific ISO week and weekday semantics must not be conflated:
/// Firebird WEEKDAY is Sunday-zero, PostgreSQL ISODOW is Monday-one.
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
    public void PostgresIsoDow_LowersToFirebirdWithMondayOneSundaySeven()
    {
        var compiled = Compile(
            "EXTRACT(ISODOW FROM DATE '2021-01-03')",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird);
        Assert.Contains("MOD(EXTRACT(WEEKDAY FROM CAST(", compiled.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" + 6, 7) + 1) AS SMALLINT)", compiled.Sql,
            StringComparison.OrdinalIgnoreCase);
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
    [InlineData("ISOYEAR")]
    public void FirebirdTarget_StillRejectsUnprovenDatePartMappings(string part)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "EXTRACT(" + part + " FROM DATE '2021-01-03')",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird));
        Assert.Contains("temporal.date_part.", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
