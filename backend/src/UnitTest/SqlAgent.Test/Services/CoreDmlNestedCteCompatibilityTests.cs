using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlNestedCteCompatibilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void CompileUpdate_ScalarSubqueryWithDerivedCte_UsesNestedCompilerAdapter(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "UPDATE users SET score = (" +
            "SELECT MAX(d.score) FROM (" +
            "WITH ranked AS (SELECT score FROM archived WHERE tenant_id = 7) " +
            "SELECT score FROM ranked) AS d) " +
            "WHERE id = 9",
            targetProvider);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileUpdate_ScalarSubqueryWithDerivedCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "UPDATE users SET score = (" +
            "SELECT MAX(d.score) FROM (" +
            "WITH ranked AS (SELECT score FROM archived) SELECT score FROM ranked) AS d) " +
            "WHERE id = 9",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void CompileUpdate_ScalarSubqueryWithRootCte_PreservesRootWithAndBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "UPDATE users SET score = (" +
            "WITH ranked AS (SELECT score FROM archived WHERE tenant_id = 7) " +
            "SELECT MAX(score) FROM ranked) WHERE id = 9",
            targetProvider);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileUpdate_ScalarSubqueryWithRootCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "UPDATE users SET score = (" +
            "WITH ranked AS (SELECT score FROM archived) SELECT MAX(score) FROM ranked) " +
            "WHERE id = 9",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scalar/EXISTS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void CompileDelete_ExistsWithDerivedCte_UsesNestedCompilerAdapter(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "DELETE FROM users WHERE id > 9 AND EXISTS (" +
            "SELECT 1 FROM (" +
            "WITH active AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT id FROM active) AS d WHERE d.id = users.id)",
            targetProvider);

        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.Contains("EXISTS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, command.Parameters.Length);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 1);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 7);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 9);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileDelete_ExistsWithDerivedCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "DELETE FROM users WHERE EXISTS (" +
            "SELECT 1 FROM (WITH active AS (SELECT id FROM archived) " +
            "SELECT id FROM active) AS d WHERE d.id = users.id)",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void CompileDelete_ExistsWithRootCte_PreservesRootWithAndBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "DELETE FROM users WHERE id > 9 AND EXISTS (" +
            "WITH active AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT 1 FROM active AS a WHERE a.id = users.id)",
            targetProvider);

        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.Contains("EXISTS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, command.Parameters.Length);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 1);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 7);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt32(parameter.Value) == 9);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileDelete_ExistsWithRootCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "DELETE FROM users WHERE EXISTS (" +
            "WITH active AS (SELECT id FROM archived) " +
            "SELECT id FROM active AS a WHERE a.id = users.id)",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scalar/EXISTS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"));
}
