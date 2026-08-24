using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreCapabilityWhitelistTests
{
    [Theory]
    [InlineData("SELECT LOWER(DISTINCT name) FROM users", "does not support DISTINCT")]
    [InlineData("SELECT LOWER(name) FILTER (WHERE id = 1) FROM users", "does not support FILTER")]
    [InlineData("SELECT LOWER(name) OVER () FROM users", "does not support OVER")]
    [InlineData("SELECT ROW_NUMBER() FROM users", "requires an OVER clause")]
    [InlineData("SELECT ABS(id, 1) FROM users", "requires 1 argument")]
    public void Compile_InvalidFunctionShapes_FailClosed(string sql, string expectedMessage)
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DistinctOnSpecializedFunction_IsNotSilentlyDropped()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT DATEADD(DISTINCT day, 1, created_at) FROM users",
                SqlAgentToolType.MsSqlServer,
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("CORE_DATE_ADD", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISTINCT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_IlikeForNonPostgresTarget_FailsBeforeLowering(SqlAgentToolType target)
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT name FROM users WHERE name ILIKE 'a%'",
                SqlAgentToolType.Postgres,
                target));

        Assert.Contains("operator.ilike", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_IlikeForPostgresTarget_RemainsSupported()
    {
        var command = Compile(
            "SELECT name FROM users WHERE name ILIKE 'a%'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("ILIKE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "a%"));
    }

    [Fact]
    public void Compile_WindowFunctionWithOver_RemainsSupported()
    {
        var command = Compile(
            "SELECT ROW_NUMBER() OVER (ORDER BY id) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("ROW_NUMBER() OVER", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 FOLLOWING AND CURRENT ROW) FROM users")]
    [InlineData("SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 1 PRECEDING) FROM users")]
    [InlineData("SELECT SUM(amount) OVER (ORDER BY id ROWS UNBOUNDED FOLLOWING) FROM users")]
    public void Compile_LogicallyInvalidWindowFrame_FailsClosed(string sql)
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres));

        Assert.Contains("Window frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_CurrentTimeForOracle_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT CURRENT_TIME FROM users",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Oracle));

        Assert.Contains("function.current_time", ex.Message, StringComparison.OrdinalIgnoreCase);
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
