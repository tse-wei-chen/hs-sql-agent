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
    [InlineData(SqlAgentToolType.Oracle)]
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

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_CteInsideSetBranch_UsesWrappedBranchAndOrderedBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT id FROM users WHERE tenant_id = 1 UNION " +
            "(WITH archived_rows AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT id FROM archived_rows WHERE id > 9)",
            targetProvider);

        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set_branch", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, command.Parameters.Length);
        Assert.Equal(1, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(7, Convert.ToInt32(command.Parameters[1].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[2].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_CteInsideSetBranch_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT id FROM users UNION " +
            "(WITH archived_rows AS (SELECT id FROM archived) SELECT id FROM archived_rows)",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_DerivedCteInsideScalarSubquery_UsesNestedCompilerAdapter(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT (SELECT MAX(d.id) FROM " +
            "(WITH active AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT id FROM active) AS d) AS value " +
            "FROM users WHERE id > 9",
            targetProvider);

        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_DerivedCteInsideScalarSubquery_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT (SELECT MAX(d.id) FROM " +
            "(WITH active AS (SELECT id FROM archived) SELECT id FROM active) AS d) AS value " +
            "FROM users",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_SetBranchCteInsideScalarSubquery_UsesNestedCompilerAdapter(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT (SELECT MAX(x.id) FROM (" +
            "SELECT id FROM archived UNION " +
            "(WITH active AS (SELECT id FROM users WHERE tenant_id = 7) SELECT id FROM active)" +
            ") AS x) AS value FROM users WHERE id > 9",
            targetProvider);

        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set_branch", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_SetBranchCteInsideScalarSubquery_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT (SELECT MAX(x.id) FROM (" +
            "SELECT id FROM archived UNION " +
            "(WITH active AS (SELECT id FROM users) SELECT id FROM active)" +
            ") AS x) AS value FROM users",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DerivedCteInsideExists_UsesNestedCompilerAdapter()
    {
        var command = Compile(
            "SELECT u.id FROM users AS u WHERE EXISTS (" +
            "SELECT 1 FROM (WITH active AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT id FROM active) AS d WHERE d.id = u.id) AND u.id > 9");

        Assert.Contains("EXISTS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, command.Parameters.Length);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 1);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 7);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 9);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_CteDefinitionLocalCte_PreservesShadowingAndOrderedBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "WITH scoped AS (SELECT id FROM users WHERE tenant_id = 1), " +
            "outer_rows AS (" +
            "WITH scoped AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT id FROM scoped WHERE id > 9" +
            ") SELECT id FROM outer_rows WHERE id < 11",
            targetProvider);

        Assert.Contains("AS (WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, command.Parameters.Length);
        Assert.Equal(1, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(7, Convert.ToInt32(command.Parameters[1].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[2].Value));
        Assert.Equal(11, Convert.ToInt32(command.Parameters[3].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_CteDefinitionLocalCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "WITH outer_rows AS (" +
            "WITH inner_rows AS (SELECT id FROM users) SELECT id FROM inner_rows" +
            ") SELECT id FROM outer_rows",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CTE-definition", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_CteDefinitionLocalSetTail_PreservesScopeTailAndBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "WITH outer_rows AS (" +
            "WITH inner_rows AS (SELECT id FROM users WHERE tenant_id = 7) " +
            "SELECT id FROM inner_rows UNION " +
            "SELECT id FROM archived WHERE tenant_id = 9 ORDER BY id LIMIT 1" +
            ") SELECT id FROM outer_rows WHERE id < 11",
            targetProvider);

        Assert.Contains("AS (SELECT * FROM (WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
        Assert.Equal(1, Convert.ToInt32(command.Parameters[2].Value));
        Assert.Equal(11, Convert.ToInt32(command.Parameters[3].Value));
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
