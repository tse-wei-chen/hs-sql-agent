using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningConflictExpressionTests
{
    [Fact]
    public void Compile_PostgresOnConflictReturningLiteralExpression_ParameterizesBeforeConflictOrdering()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO NOTHING RETURNING id + 2 AS projected_id",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        var conflictIndex = command.Sql.IndexOf(" ON CONFLICT ", StringComparison.OrdinalIgnoreCase);
        var returningIndex = command.Sql.IndexOf(" RETURNING ", StringComparison.OrdinalIgnoreCase);
        Assert.True(conflictIndex >= 0);
        Assert.True(returningIndex > conflictIndex);
        Assert.DoesNotContain("Alice", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" + 2", command.Sql, StringComparison.Ordinal);
        Assert.Equal([1, "Alice", 2], command.Parameters.Select(parameter => parameter.Value).ToArray());
    }
}
