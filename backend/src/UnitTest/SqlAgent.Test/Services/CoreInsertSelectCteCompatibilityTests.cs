using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreInsertSelectCteCompatibilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_InsertSelectWithRootCte_UsesProviderPlacementAndOrderedBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "INSERT INTO archived (id) " +
            "WITH active AS (SELECT id FROM users WHERE tenant_id = 7) " +
            "SELECT id FROM active WHERE id > 9",
            targetProvider);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active", command.Sql, StringComparison.OrdinalIgnoreCase);

        var withIndex = command.Sql.IndexOf("WITH ", StringComparison.OrdinalIgnoreCase);
        var insertIndex = command.Sql.IndexOf("INSERT INTO", StringComparison.OrdinalIgnoreCase);
        if (targetProvider is SqlAgentToolType.Postgres or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite)
            Assert.True(withIndex >= 0 && withIndex < insertIndex, command.Sql);
        else
            Assert.True(insertIndex >= 0 && insertIndex < withIndex, command.Sql);

        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_InsertSelectRootCteSetTail_PreservesCteAndOuterTail(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "INSERT INTO archived (id) " +
            "WITH active AS (SELECT id FROM users) " +
            "SELECT id FROM active UNION SELECT id FROM users ORDER BY id LIMIT 3",
            targetProvider);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_set", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectWithDerivedLocalCte_FailsBeforeSqlKataCanDropDefinition()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archived (id) " +
            "SELECT d.id FROM (WITH active AS (SELECT id FROM users) SELECT id FROM active) AS d"));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("derived-table-local", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectWithSetBranchLocalCte_FailsBeforeSqlKataCanDropDefinition()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archived (id) " +
            "SELECT id FROM users UNION " +
            "(WITH active AS (SELECT id FROM archived) SELECT id FROM active)"));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set-operation-branch-local", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectWithoutCte_RemainsSupported()
    {
        var command = Compile(
            "INSERT INTO archived (id) SELECT id FROM users");

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.StartsWith("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider = SqlAgentToolType.Postgres) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"));
}
