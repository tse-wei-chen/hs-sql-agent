using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningSimpleCaseTests
{
    [Fact]
    public void Compile_PostgresSimpleCaseReturning_ParameterizesMatchAndResultLiterals()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING CASE status WHEN 'active' THEN 1 ELSE 0 END AS status_code",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status_code", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", command.Sql, StringComparison.Ordinal);
        Assert.Equal(4, command.Parameters.Length);
        Assert.Equal(9L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal("active", command.Parameters[1].Value);
        Assert.Equal(1L, Convert.ToInt64(command.Parameters[2].Value));
        Assert.Equal(0L, Convert.ToInt64(command.Parameters[3].Value));
    }

    [Fact]
    public void Compile_PostgresSearchedCaseReturning_ValidatesPredicateSubsetAndParameterizes()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING CASE WHEN id = 1 THEN 'one' WHEN id BETWEEN 2 AND 3 THEN 'few' ELSE 'other' END AS bucket",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains("CASE WHEN", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bucket", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other", command.Sql, StringComparison.Ordinal);
        Assert.Equal(7, command.Parameters.Length);
        Assert.Equal(9L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal(1L, Convert.ToInt64(command.Parameters[1].Value));
        Assert.Equal("one", command.Parameters[2].Value);
        Assert.Equal(2L, Convert.ToInt64(command.Parameters[3].Value));
        Assert.Equal(3L, Convert.ToInt64(command.Parameters[4].Value));
        Assert.Equal("few", command.Parameters[5].Value);
        Assert.Equal("other", command.Parameters[6].Value);
    }

    [Fact]
    public void Compile_PostgresSearchedCaseReturning_SubqueryPredicateRemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING id",
            SqlAgentToolType.Postgres);
        var subquery = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users",
            SqlAgentToolType.Postgres).Statement;
        var searchedCase = new CaseExpr(
            [new CaseBranch(
                new ExistsExpr(subquery, false, SourceSpan.Unknown),
                new LiteralExpr(1, SourceSpan.Unknown))],
            new LiteralExpr(0, SourceSpan.Unknown),
            SourceSpan.Unknown);
        var delete = Assert.IsType<DeleteStatement>(parsed.Statement);
        delete.Returning = [new DmlReturningExpressionItem(searchedCase, null, SourceSpan.Unknown)];
        parsed.Statement = delete;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("ExistsExpr", error.Message, StringComparison.Ordinal);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
