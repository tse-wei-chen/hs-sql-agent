using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeTemporalGrammarMutationMatrixTests
{
    public const int ExpectedCaseCount = 5;

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Extract_WrongSourceDialect_FailsClosedAtSourceValidation(
        SqlAgentToolType sourceDialect)
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT EXTRACT(HOUR FROM created_at) FROM events",
                    sourceDialect),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("negative-temporal-source-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("EXTRACT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void DateDiff_NonDayCrossDialect_RemainsFailClosed(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT DATEDIFF(HOUR, created_at, updated_at) FROM events",
                    SqlAgentToolType.MsSqlServer),
                targetProvider,
                new SqlPlanValidationContext("negative-temporal-diff-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("Cross-dialect DATEDIFF", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HOUR", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
