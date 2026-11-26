using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>ISO-8601 week 1..53 is not MySQL's session-default WEEK mode
/// or SQLite's non-ISO %W calendar week.</summary>
public sealed class SqlCoreIsoWeekCrossProviderTests
{
    private static CompiledSqlCommand Compile(SqlAgentToolType source, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(
            "SELECT EXTRACT(WEEK FROM DATE '2021-01-01') AS iso_week",
            source, target, new SqlPlanValidationContext("iso-week-cross-provider"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlAgentToolType.MySQL, "WEEK(CAST(", ", 3)")]
    [InlineData(SqlAgentToolType.Firebird, SqlAgentToolType.MySQL, "WEEK(CAST(", ", 3)")]
    [InlineData(SqlAgentToolType.Postgres, SqlAgentToolType.Sqlite, "STRFTIME('%j', DATE(", "'weekday 4'")]
    [InlineData(SqlAgentToolType.Firebird, SqlAgentToolType.Sqlite, "STRFTIME('%j', DATE(", "'weekday 4'")]
    public void ProvenIsoWeekSources_UseSemanticTargetLowering(
        SqlAgentToolType source, SqlAgentToolType target,
        string nativeFunction, string isoAdjustment)
    {
        var command = Compile(source, target);
        Assert.Contains(nativeFunction, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(isoAdjustment, command.Sql, StringComparison.OrdinalIgnoreCase);
        if (target == SqlAgentToolType.Sqlite)
        {
            Assert.Contains("/ 7) + 1", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("AS INTEGER)", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "default_week_format")]
    [InlineData(SqlAgentToolType.Sqlite, "not native SQL Server or SQLite source syntax")]
    [InlineData(SqlAgentToolType.MsSqlServer, "not native SQL Server or SQLite source syntax")]
    [InlineData(SqlAgentToolType.Oracle, "date part 'WEEK'")]
    public void NonIsoSourceExtractWeek_RejectsAtParsing(
        SqlAgentToolType source, string expectedDiagnostic)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT EXTRACT(WEEK FROM order_date) FROM orders", source));
        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlAgentToolType.MsSqlServer, "DATEPART(ISO_WEEK,")]
    [InlineData(SqlAgentToolType.Firebird, SqlAgentToolType.MsSqlServer, "DATEPART(ISO_WEEK,")]
    [InlineData(SqlAgentToolType.Postgres, SqlAgentToolType.Oracle, "TO_NUMBER(TO_CHAR(")]
    [InlineData(SqlAgentToolType.Firebird, SqlAgentToolType.Oracle, "TO_NUMBER(TO_CHAR(")]
    public void SqlServerAndOracleTargets_UseNativeIsoWeek(
        SqlAgentToolType source, SqlAgentToolType target, string nativeSql)
    {
        var command = Compile(source, target);
        Assert.Contains(nativeSql, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DATEPART(WEEK,", command.Sql, StringComparison.OrdinalIgnoreCase);
        if (target == SqlAgentToolType.Oracle)
            Assert.Contains("'IW'", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicIsoWeekMatrix_AgreesWithTargetProof()
    {
        var expectations = new (SqlAgentToolType Provider, SqlCapabilityStatus Status)[]
        {
            (SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Supported),
            (SqlAgentToolType.Oracle, SqlCapabilityStatus.Supported)
        };
        foreach (var (provider, expected) in expectations)
        {
            var cap = Assert.Single(SqlCapabilityMatrix.ForProvider(provider).Capabilities,
                c => c.Id == "temporal.date_part.iso_week");
            Assert.Equal(expected, cap.Status);
        }
    }

    // DATEPART(WEEK) and WEEK(date) are native but session-dependent, not ISO.
    // The already-proven ISO target must not make these source semantics valid.
    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "DATEPART(WEEK, order_date)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "DATEPART(WK, order_date)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "DATEPART(WW, order_date)")]
    [InlineData(SqlAgentToolType.MySQL, "WEEK(order_date)")]
    public void SessionDependentNativeWeek_CannotBecomeIsoWeek(SqlAgentToolType source, string expression)
    {
        var error = Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileQuery(
            "SELECT " + expression + " FROM orders", source, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("iso-week-source-functions"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken));
        Assert.Contains(source == SqlAgentToolType.MsSqlServer ? "DATEFIRST" : "default_week_format",
            error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ISO_WEEK")]
    [InlineData("ISOWK")]
    [InlineData("ISOWW")]
    public void SqlServerNativeIsoWeekAliases_PreserveIsoSourceMeaning(string part)
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT DATEPART(" + part + ", order_date) FROM orders",
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("iso-week-source-functions"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
        Assert.Contains("EXTRACT(WEEK FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgresNativeDatePartWeek_RemainsIso()
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT DATE_PART('week', order_date) FROM orders",
            SqlAgentToolType.Postgres, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("iso-week-source-functions"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
        Assert.Contains("WEEK(CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(", 3)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

}
