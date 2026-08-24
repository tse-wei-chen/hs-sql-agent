using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSourceDialectValidationTests
{
    [Fact]
    public void Compile_DateAdd_WithPostgresSource_FailsBeforeCanonicalization()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT DATEADD(DAY, 1, created_at) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL));

        Assert.Contains("source dialect Postgres", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATEADD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DateAdd_WithSqlServerSource_RemainsPortable()
    {
        var command = CompileQuery(
            "SELECT DATEADD(DAY, 1, created_at) FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MySQL);

        Assert.Contains("TIMESTAMPADD", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlTwoArgumentDateDiff_RemainsPortable()
    {
        var command = CompileQuery(
            "SELECT DATEDIFF(completed_at, created_at) FROM orders",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres);

        Assert.NotEmpty(command.Sql);
        Assert.DoesNotContain("CORE_DATE_DIFF", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresTwoArgumentDateDiff_IsRejectedAsRawSourceSyntax()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT DATEDIFF(completed_at, created_at) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("DATEDIFF", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect Postgres", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlFormat_IsNotMisreadAsSqlServerDateFormat()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT FORMAT(amount, 2) FROM orders",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("FORMAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("different semantics", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DateFormat_WithSqlServerSource_IsRejectedAsInvalidSourceSyntax()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT DATE_FORMAT(created_at, '%Y-%m-%d') FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MySQL));

        Assert.Contains("DATE_FORMAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MsSqlServer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_GroupConcat_WithPostgresSource_IsRejectedBeforeTargetTranslation()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT GROUP_CONCAT(name) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL));

        Assert.Contains("GROUP_CONCAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect Postgres", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_CurrentDate_WithSqlServerSource_IsRejectedButCurrentTimestampIsAllowed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT CURRENT_DATE FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres));
        Assert.Contains("CURRENT_DATE", ex.Message, StringComparison.OrdinalIgnoreCase);

        var command = CompileQuery(
            "SELECT CURRENT_TIMESTAMP FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);
        Assert.Contains("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Dml_UsesTheSameSourceDialectBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseDml(
                    "DELETE FROM orders WHERE DATE_FORMAT(created_at, '%Y') = '2026'",
                    SqlAgentToolType.MsSqlServer),
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("policy-v1"),
                new DmlCompilationPolicy()));

        Assert.Contains("DATE_FORMAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MsSqlServer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
