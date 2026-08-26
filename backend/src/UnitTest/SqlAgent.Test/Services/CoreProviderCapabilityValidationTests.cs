using Xunit;

namespace SqlAgent.Test.Services;

public class CoreProviderCapabilityValidationTests
{
    [Fact]
    public void Compile_NthValueForSqlServer_FailsBeforeLowering()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT NTH_VALUE(amount, 1) OVER (ORDER BY id) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("function.nth_value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerWindowFunctionWithoutOrderBy_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT ROW_NUMBER() OVER () FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("window.order_by", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DistinctWindowAggregate_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SUM(DISTINCT amount) OVER (ORDER BY id) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("DISTINCT window aggregate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRankingFunctionWithExplicitFrame_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT ROW_NUMBER() OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("window.frame.row_number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRangeOffsetFrame_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SUM(amount) OVER (ORDER BY id RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("window.range_offset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_JsonExtractForOracle_FailsBeforeLowering()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT JSON_EXTRACT(payload, '$.id') FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Oracle));

        Assert.Contains("function.json_extract", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RegexForSqlServer_FailsBeforeLowering()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT REGEXP_LIKE(name, '^A') FROM users",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("function.regex_match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NonDayDateAddForPostgres_FailsBeforeLowering()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT DATEADD(month, 1, created_at) FROM events",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres));

        Assert.Contains("core_date_add.unit.month", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_QuarterDateAddForFirebird_FailsBeforeLowering()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT DATEADD(quarter, 1, created_at) FROM events",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Firebird));

        Assert.Contains("core_date_add.unit.quarter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_TimeLiteralForOracle_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT TIME '12:34:56'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle));

        Assert.Contains("literal.time", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
