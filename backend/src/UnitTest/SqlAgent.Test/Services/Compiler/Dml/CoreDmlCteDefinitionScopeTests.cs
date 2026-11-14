using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlCteDefinitionScopeTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_InsertRootCteWithDefinitionLocalCte_PreservesNestedScopeAndBindings(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "INSERT INTO archived (id) " +
            "WITH outer_rows AS (" +
            "WITH local_rows AS (SELECT id FROM users WHERE tenant_id = 7) " +
            "SELECT id FROM local_rows WHERE id > 9" +
            ") SELECT id FROM outer_rows",
            targetProvider);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("AS (WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
        Assert.Equal(9, Convert.ToInt32(command.Parameters[1].Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_InsertRootCteWithDefinitionLocalCte_FailsClosedWithoutDeclaredTargetGrammar(
        SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archived (id) " +
            "WITH outer_rows AS (" +
            "WITH local_rows AS (SELECT id FROM users) SELECT id FROM local_rows" +
            ") SELECT id FROM outer_rows",
            targetProvider));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CTE-definition", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"));
}
