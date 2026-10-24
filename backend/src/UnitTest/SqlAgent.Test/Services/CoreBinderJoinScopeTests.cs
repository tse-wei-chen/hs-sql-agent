using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreBinderJoinScopeTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_WideSequentialJoins_PreserveSourceScopeAndParameterOrder(
        SqlAgentToolType provider)
    {
        const int joinCount = 16;
        var joins = string.Concat(Enumerable.Range(1, joinCount).Select(index =>
            $" JOIN users AS j{index} ON j{index}.id = r.id AND j{index}.tenant_id = {index}"));
        var sql = "SELECT r.id FROM users AS r" + joins + " WHERE r.id = 99";

        var command = Compile(sql, provider);
        Assert.Equal(joinCount, command.Sql.Split(" JOIN ", StringSplitOptions.None).Length - 1);
        Assert.Equal(joinCount + 1, command.Parameters.Length);
        Assert.Equal(
            Enumerable.Range(1, joinCount).Select(index => (long)index).Append(99L),
            command.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_DuplicateJoinAlias_PreservesFirstAliasBindingDiagnostic(
        SqlAgentToolType provider)
    {
        var sql = "SELECT r.id FROM users AS r "
            + "JOIN teams AS shared ON shared.id = r.id "
            + "JOIN accounts AS SHARED ON SHARED.id = r.id";

        var error = Assert.Throws<InvalidOperationException>(() => Compile(sql, provider));
        Assert.Contains("Duplicate table alias 'shared'", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.Data["HsSqlAgent.SqlCore.Diagnostic"]);
    }


    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_StreamingCorrelatedExists_PreservesProjectionFirstParameters(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT 5 AS top_value FROM users u WHERE EXISTS ("
            + "SELECT 10 FROM users v WHERE EXISTS ("
            + "SELECT 20 FROM users w WHERE w.id = 30))";
        var compiled = Compile(sql, dialect);
        Assert.Equal(new long[] { 5, 10, 20, 30 },
            compiled.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Contains("EXISTS", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StreamingJoinedCorrelatedLateral_PreservesOnAndWhereParameterOrder()
    {
        var sql = "SELECT 11 AS outer_key FROM users u JOIN LATERAL ("
            + "SELECT u.id AS id WHERE u.id = 22) l ON l.id = 33 WHERE u.id = 44";
        var compiled = Compile(sql, SqlAgentToolType.Postgres);
        Assert.Equal(new long[] { 11, 22, 33, 44 },
            compiled.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Contains("LATERAL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_StreamingInSubquery_PreservesProjectionFirstParameters(SqlAgentToolType dialect)
    {
        var sql = "SELECT 11 FROM users WHERE id IN ("
            + "SELECT 22 FROM users WHERE id IN (SELECT 33 FROM users))";
        var compiled = Compile(sql, dialect);
        Assert.Equal(new long[] { 11, 22, 33 },
            compiled.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType provider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, provider),
            provider,
            new SqlPlanValidationContext("binder-join-scope-iterative-v1"),
            new SqlExecutionPlanPolicy());
}
