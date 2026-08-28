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
    public void Compile_PostgresSearchedCaseReturning_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 9 RETURNING CASE WHEN id = 1 THEN 1 ELSE 0 END",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("CaseExpr", error.Message, StringComparison.Ordinal);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
