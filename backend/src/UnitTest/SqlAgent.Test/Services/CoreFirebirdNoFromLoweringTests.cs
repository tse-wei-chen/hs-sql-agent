using Xunit;

namespace SqlAgent.Test.Services;

public class CoreFirebirdNoFromLoweringTests
{
    [Fact]
    public void Compile_NoFromSelect_ToFirebird_UsesSingleRowDatabaseTable()
    {
        var command = Compile(
            "SELECT 1 AS one",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Firebird);

        Assert.Contains("FROM RDB$DATABASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
    }

    [Fact]
    public void Compile_SelectWithFrom_ToFirebird_DoesNotAddSingleRowDatabaseTable()
    {
        var command = Compile(
            "SELECT id FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Firebird);

        Assert.Contains("ORDERS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RDB$DATABASE", command.Sql, StringComparison.OrdinalIgnoreCase);
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
