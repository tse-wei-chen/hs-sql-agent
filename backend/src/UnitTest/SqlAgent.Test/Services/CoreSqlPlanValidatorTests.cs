using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlPlanValidatorTests
{
    [Fact]
    public void Compile_Whitelist_AllowsReferencedTable()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM sales.orders",
            SqlAgentToolType.Postgres);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sales.orders" }),
            new SqlExecutionPlanPolicy());

        Assert.Equal(SqlStatementKind.Query, command.Kind);
        Assert.Contains("sales", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Whitelist_RejectsPhysicalTableOutsideWhitelist()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM sales.orders",
            SqlAgentToolType.Postgres);

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext(
                    "policy-v1",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "crm.customers" }),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("sales.orders", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_CteColumnAliases_LowerThroughProjectionAliases(SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH recent(id) AS (SELECT order_id FROM orders) SELECT id FROM recent",
            provider);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            provider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recent", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WITH recent(id, total) AS (SELECT order_id FROM orders) SELECT id FROM recent", "declares 2 column alias")]
    [InlineData("WITH recent(id) AS (SELECT * FROM orders) SELECT id FROM recent", "contains a wildcard")]
    public void Compile_CteColumnAliases_WithUnknownOrMismatchedWidth_FailClosed(
        string sql,
        string expectedMessage)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
