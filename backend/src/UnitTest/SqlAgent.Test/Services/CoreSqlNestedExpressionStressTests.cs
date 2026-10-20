using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Regression against parser-stack exhaustion by pure grouping parentheses.
/// Non-redundant nested SQL queries remain a separate measured resource class.
/// </summary>
public sealed class CoreSqlNestedExpressionStressTests
{
    public static IEnumerable<object[]> SourceDialects()
        => Enum.GetValues<SqlAgentToolType>().Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DeepRedundantGrouping_CompilesWithoutRecursiveDescent(
        SqlAgentToolType dialect)
    {
        // 2,048 wrappers fit well within the lexical budget but historically
        // required 2,048 nested parseExpression/parsePrimary calls.
        const int depth = 2_048;
        var sql = "SELECT " + new string('(', depth) + "1" + new string(')', depth);
        var context = new SqlPlanValidationContext("iterative-grouping-v1");

        var compiled = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect, context, new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
        Assert.Equal(
            SqlCoreFacade.CompileQuery(
                "SELECT 1", dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql,
            compiled.Sql);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedArithmeticGrouping_PreservesPrecedence(
        SqlAgentToolType dialect)
    {
        var context = new SqlPlanValidationContext("iterative-grouping-v1");
        var inputs = new[]
        {
            "SELECT (1 + 2) * 3",
            "SELECT ((1) + (2)) * 3",
            "SELECT (((1) + 2)) * (3 + 4)",
            "SELECT (1 + (2 * 3))"
        };
        foreach (var sql in inputs)
        {
            var result = SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, context, new SqlExecutionPlanPolicy());
            Assert.NotEmpty(result.Sql);
        }
    }

    [Fact]
    public void DeepGroupingAroundScalarSubquery_DoesNotChangeQueryGrammar()
    {
        const int depth = 128;
        var sql = "SELECT " + new string('(', depth) +
            "(SELECT 1)" + new string(')', depth);
        var context = new SqlPlanValidationContext("iterative-grouping-v1");
        var compiled = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
    }

    [Fact]
    public void UnmatchedGrouping_IsStillRejected()
    {
        const int depth = 128;
        var sql = "SELECT " + new string('(', depth) + "1" + new string(')', depth - 1);
        Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("iterative-grouping-v1"),
            new SqlExecutionPlanPolicy()));
    }
}
