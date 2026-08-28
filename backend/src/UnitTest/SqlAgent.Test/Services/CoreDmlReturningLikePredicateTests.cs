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
    public void Compile_PostgresReturningLikeWithExplicitEscape_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING name LIKE 'A!_%' ESCAPE '!'",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
