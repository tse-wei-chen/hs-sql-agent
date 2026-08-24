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
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_ScalarSubqueryWithRootCte_FailsBeforeSqlKataCanDropDefinition(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT (WITH active AS (SELECT id FROM archived) " +
            "SELECT MAX(id) FROM active) AS value FROM users",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("statement-root WITH", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_ExistsSubqueryWithRootCte_FailsBeforeSqlKataCanDropDefinition(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT u.id FROM users AS u WHERE EXISTS (" +
            "WITH active AS (SELECT id FROM archived) " +
            "SELECT id FROM active WHERE id = u.id)",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("statement-root WITH", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"));
}
