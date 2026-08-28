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
        Assert.Equal(new object?[] { 9, "active", 1, 0 }, command.Parameters.Select(x => x.Value).ToArray());
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
        Assert.Equal(
            new object?[] { 9, 1, "one", 2, 3, "few", "other" },
            command.Parameters.Select(x => x.Value).ToArray());
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
                new ExistsExpr(subquery, IsNegated: false, SourceSpan.Unknown),
                new LiteralExpr(1, SourceSpan.Unknown))],
            new LiteralExpr(0, SourceSpan.Unknown),
            SourceSpan.Unknown);
        var delete = Assert.IsType<DeleteStatement>(parsed.Statement);
        parsed = parsed with
        {
            Statement = delete with
            {
                Returning = [new DmlReturningExpressionItem(searchedCase, null, SourceSpan.Unknown)]
            }
        };

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("ExistsExpr", error.Message, StringComparison.Ordinal);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
