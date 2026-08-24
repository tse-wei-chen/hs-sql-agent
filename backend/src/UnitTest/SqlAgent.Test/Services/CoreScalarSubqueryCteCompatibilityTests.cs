using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreScalarSubqueryCteCompatibilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_ScalarSubqueryWithRootCte_PreservesRootWithAndCorrelation(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT (WITH active AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT MAX(a.id) FROM active AS a WHERE a.id <= users.id) AS value " +
            "FROM users WHERE id > 9",
            targetProvider);

        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_ScalarSubqueryWithRootCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT (WITH active AS (SELECT id FROM archived) " +
            "SELECT MAX(id) FROM active) AS value FROM users",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scalar/EXISTS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_ExistsSubqueryWithRootCte_PreservesRootWithAndBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT u.id FROM users AS u WHERE u.id > 9 AND EXISTS (" +
            "WITH active AS (SELECT id FROM archived WHERE tenant_id = 7) " +
            "SELECT 1 FROM active AS a WHERE a.id = u.id)",
            targetProvider);

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
    public void Compile_ExistsSubqueryWithRootCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT u.id FROM users AS u WHERE EXISTS (" +
            "WITH active AS (SELECT id FROM archived) " +
            "SELECT id FROM active WHERE id = u.id)",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scalar/EXISTS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_ScalarRootCteSetTail_RemainsFailClosedUntilRecursiveRewriteExists(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT (WITH active AS (SELECT id FROM archived) " +
            "SELECT id FROM active UNION SELECT id FROM users ORDER BY id LIMIT 1) AS value " +
            "FROM users",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outer ORDER BY/LIMIT/OFFSET", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(0));
}
