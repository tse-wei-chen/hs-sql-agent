using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreOracle26AggregateFilterDmlTests
{
    [Fact]
    public void Compile_InsertSelectOracle26Target_WithLocalFilterPredicate_Compiles()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "INSERT INTO order_totals (amount) SELECT SUM(amount) FILTER (WHERE status = 'open') FROM orders",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy(),
            targetProfile: Oracle26Profile());

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectOracle26Target_WithSubqueryFilterPredicate_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseDml(
                    "INSERT INTO order_totals (amount) SELECT SUM(amount) FILTER (WHERE EXISTS (SELECT id FROM customers)) FROM orders",
                    SqlAgentToolType.Postgres),
                SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("policy-v1"),
                new DmlCompilationPolicy(),
                targetProfile: Oracle26Profile()));

        Assert.Contains("Oracle 26ai target", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subqueries", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlProviderCapabilityProfile Oracle26Profile() =>
        new(SqlAgentToolType.Oracle, new Version(26, 0));
}
