using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreCteBackendCompatibilityTests
{
    [Fact]
    public void Compile_CteInsideDerivedTable_FailsBeforeSqlKataCanDropDefinition()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT d.id FROM (WITH active AS (SELECT id FROM users) SELECT id FROM active) AS d"));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("derived-table-local", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_CteInsideSetBranch_FailsBeforeSqlKataCanDropDefinition()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT id FROM users UNION (WITH archived_rows AS (SELECT id FROM archived) SELECT id FROM archived_rows)"));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set-operation-branch-local", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_RootCteWithSetOuterOrderBy_PreservesCteOnOuterWrapper(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "WITH active AS (SELECT id FROM users) " +
            "SELECT id FROM active UNION SELECT id FROM archived ORDER BY id",
            targetProvider);

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RootCteSetTail_PreservesProjectedAliasOrdering()
    {
        var command = Compile(
            "WITH active AS (SELECT id FROM users) " +
            "SELECT id AS key FROM active UNION SELECT id FROM archived ORDER BY key");

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RootCteSetTail_PreservesOutputOrdinalOrdering()
    {
        var command = Compile(
            "WITH active AS (SELECT id FROM users) " +
            "SELECT id FROM active UNION SELECT id FROM archived ORDER BY 1");

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY 1", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RootCteSetWithPolicyLimit_PreservesCteOnGeneratedWrapper()
    {
        var command = Compile(
            "WITH active AS (SELECT id FROM users) " +
            "SELECT id FROM active UNION SELECT id FROM archived",
            SqlAgentToolType.Postgres,
            queryMaxRows: 5);

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RootCteSelect_RemainsSupported()
    {
        var command = Compile(
            "WITH active AS (SELECT id FROM users) SELECT id FROM active");

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RootCteSetWithoutOuterTail_RemainsSupported()
    {
        var command = Compile(
            "WITH active AS (SELECT id FROM users) " +
            "SELECT id FROM active UNION SELECT id FROM archived");

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider = SqlAgentToolType.Postgres,
        int queryMaxRows = 0) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(queryMaxRows));
}
