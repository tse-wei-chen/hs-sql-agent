using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningPredicateExpressionTests
{
    [Fact]
    public void Compile_PostgresTopLevelComparisonReturning_ParameterizesPredicateLiteral()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING id = 9 AS matched",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains("matched", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" = 9", command.Sql, StringComparison.Ordinal);
        Assert.Equal(new object?[] { 9, 9 }, command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void Compile_PostgresTopLevelFiniteInReturning_ParameterizesAllItems()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING id IN (1, 2, 3) AS small_id",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains(" IN ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new object?[] { 9, 1, 2, 3 }, command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void Compile_PostgresTopLevelLikeReturning_ParameterizesPatternLiteral()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING name LIKE 'a%' AS matches_name",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains(" LIKE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matches_name", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a%", command.Sql, StringComparison.Ordinal);
        Assert.Equal(new object?[] { 9, "a%" }, command.Parameters.Select(x => x.Value).ToArray());
    }
}
