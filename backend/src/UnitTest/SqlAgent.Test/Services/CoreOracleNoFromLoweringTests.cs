using Xunit;

namespace SqlAgent.Test.Services;

public class CoreOracleNoFromLoweringTests
{
    [Fact]
    public void Compile_NoFromSelect_ToOracle_UsesDual()
    {
        var command = Compile(
            "SELECT 1 AS one",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.Contains("FROM DUAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(1, command.Parameters[0].Value);
    }

    [Fact]
    public void Compile_SelectWithFrom_ToOracle_DoesNotAddDual()
    {
        var command = Compile(
            "SELECT id FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.Contains("ORDERS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DUAL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
