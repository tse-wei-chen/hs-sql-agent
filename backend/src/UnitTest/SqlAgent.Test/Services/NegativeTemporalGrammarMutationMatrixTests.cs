using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeTemporalGrammarMutationMatrixTests
{
    public const int ExpectedDateAddRejectionCaseCount = 18;
    public const int ExpectedCaseCount = 23;

    public static IEnumerable<object[]> UnsupportedDateAddCases()
    {
        var targets = new[]
        {
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Oracle
        };
        var units = new[] { "MONTH", "QUARTER", "YEAR" };
        var contexts = new[] { "select", "predicate", "order" };

        foreach (var target in targets)
        foreach (var unit in units)
        foreach (var context in contexts)
            yield return [target, unit, context];
    }

    [Theory]
    [MemberData(nameof(UnsupportedDateAddCases))]
    public void DateAdd_CalendarUnitsWithoutRolloverProof_FailClosed(
        SqlAgentToolType targetProvider,
        string unit,
        string context)
    {
        var expression = $"DATEADD({unit}, 2, created_at)";
        var sql = context switch
        {
            "select" => $"SELECT {expression} AS shifted FROM events",
            "predicate" => $"SELECT id FROM events WHERE {expression} > created_at",
            "order" => $"SELECT id FROM events ORDER BY {expression}",
            _ => throw new ArgumentOutOfRangeException(nameof(context))
        };

        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MsSqlServer),
                targetProvider,
                new SqlPlanValidationContext("negative-temporal-dateadd-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(
            $"core_date_add.unit.{unit.ToLowerInvariant()}",
            ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

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
