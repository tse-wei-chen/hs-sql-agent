using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreCteBackendCompatibilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_CteInsideDerivedTable_UsesFullNestedCompilation(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT d.id FROM " +
            "(WITH active AS (SELECT id FROM users WHERE tenant_id = 7) " +
            "SELECT id FROM active WHERE id > 9) AS d",
            targetProvider);

        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("d", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_CteInsideDerivedTable_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT d.id FROM " +
            "(WITH active AS (SELECT id FROM users) SELECT id FROM active) AS d",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_CteInsideJoinedDerivedTable_PreservesNestedThenOuterBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT u.id FROM users AS u " +
            "JOIN (WITH active AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT id FROM active) AS d ON d.id = u.id " +
            "WHERE u.id > 9",
            targetProvider);

        Assert.Contains("JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_DerivedRootCteSetTail_PreservesNestedScopeAndTail(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT d.id FROM (" +
            "WITH active AS (SELECT id FROM users) " +
            "SELECT id FROM active UNION SELECT id FROM archived ORDER BY id LIMIT 2" +
            ") AS d",
            targetProvider);

        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_CteInsideSetBranch_RemainsFailClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT id FROM users UNION " +
            "(WITH archived_rows AS (SELECT id FROM archived) SELECT id FROM archived_rows)"));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set-operation-branch-local", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DerivedCteInsideScalarSubquery_RemainsFailClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT (SELECT d.id FROM " +
            "(WITH active AS (SELECT id FROM archived) SELECT id FROM active) AS d LIMIT 1) AS value " +
            "FROM users"));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
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
