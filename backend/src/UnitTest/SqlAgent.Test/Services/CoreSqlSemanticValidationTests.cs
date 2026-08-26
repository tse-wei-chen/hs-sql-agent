using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlSemanticValidationTests
{
    [Theory]
    [InlineData("SELECT id FROM orders WHERE SUM(amount) > 10", "Aggregate function 'SUM'")]
    [InlineData("SELECT id FROM orders GROUP BY SUM(amount)", "Aggregate function 'SUM'")]
    [InlineData("SELECT id FROM orders WHERE ROW_NUMBER() OVER (ORDER BY id) = 1", "Window expressions")]
    [InlineData("SELECT SUM(AVG(amount)) FROM orders", "cannot be nested")]
    public void Compile_InvalidAggregateOrWindowPlacement_FailsClosed(string sql, string expectedMessage)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(sql, SqlAgentToolType.Postgres));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_AggregateInHaving_RemainsSupported()
    {
        var command = Compile(
            "SELECT customer_id, SUM(amount) AS total FROM orders GROUP BY customer_id HAVING SUM(amount) > 10",
            SqlAgentToolType.Postgres);

        Assert.Contains("SUM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAVING", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FullJoinForMySqlTarget_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT a.id FROM a FULL JOIN b ON a.id = b.id",
            SqlAgentToolType.MySQL));

        Assert.Contains("join.full", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationWithKnownMismatchedProjectionWidths_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT id FROM users UNION SELECT id, name FROM archived_users",
            SqlAgentToolType.Postgres));

        Assert.Contains("projection width", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationWithMatchingProjectionWidths_RemainsSupported()
    {
        var command = Compile(
            "SELECT id FROM users UNION SELECT id FROM archived_users",
            SqlAgentToolType.Postgres);

        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType provider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, provider),
            provider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
