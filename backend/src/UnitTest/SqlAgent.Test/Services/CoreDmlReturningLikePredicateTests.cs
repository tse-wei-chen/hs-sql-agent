using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningLikePredicateTests
{
    [Theory]
    [InlineData("LIKE", "A%")]
    [InlineData("ILIKE", "a%")]
    public void Compile_PostgresReturningLikePredicate_ParameterizesPattern(
        string predicateOperator,
        string pattern)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            $"DELETE FROM users WHERE id = 9 RETURNING name {predicateOperator} '{pattern}' AS matches_name",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains(predicateOperator, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pattern, command.Sql, StringComparison.Ordinal);
        Assert.Equal(new object?[] { 9, pattern }, command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void Compile_PostgresReturningLikeWithExplicitEscape_RendersValidatedEscape()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING name LIKE 'A!_%' ESCAPE '!' AS matches_name",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains("ESCAPE '!'", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A!_%", command.Sql, StringComparison.Ordinal);
        Assert.Equal(new object?[] { 9, "A!_%" }, command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void Compile_PostgresReturningLikeWithInvalidEscape_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING id",
            SqlAgentToolType.Postgres);
        var delete = Assert.IsType<DeleteStatement>(parsed.Statement);
        var expression = new BinaryExpr(
            new ColumnExpr(SqlIdentifier.Unquoted("name"), SourceSpan.Unknown),
            "LIKE",
            new LiteralExpr("A!_%", SourceSpan.Unknown),
            SourceSpan.Unknown,
            LikeEscape: "!!");
        parsed = parsed with
        {
            Statement = delete with
            {
                Returning = [new DmlReturningExpressionItem(expression, null, SourceSpan.Unknown)]
            }
        };

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("ESCAPE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one non-control character", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
