using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreNativePaginationRangeTests
{
    [Fact]
    public void Compile_SqlServerMaxIntOffsetOnly_DoesNotOverflowRowNumberLowerBound()
    {
        var command = Compile(
            "SELECT id FROM users ORDER BY id OFFSET 2147483647",
            SqlAgentToolType.MsSqlServer);

        var bound = Assert.Single(command.Parameters);
        Assert.Equal(2147483648L, Convert.ToInt64(bound.Value));
        Assert.True(Convert.ToInt64(bound.Value) > 0);
    }

    [Fact]
    public void Compile_SqlServerMaxIntLimitAndOffset_DoesNotOverflowRowNumberRange()
    {
        var command = Compile(
            "SELECT id FROM users ORDER BY id LIMIT 2147483647 OFFSET 2147483647",
            SqlAgentToolType.MsSqlServer);

        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(2147483648L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal(4294967294L, Convert.ToInt64(command.Parameters[1].Value));
    }

    [Fact]
    public void Compile_FirebirdSetLimitOnly_PreservesCanonicalLimit()
    {
        var command = CompileFirebirdSource(
            "SELECT id FROM users UNION ALL SELECT id FROM admins ORDER BY id FETCH FIRST 5 ROWS ONLY");

        Assert.Contains("SELECT FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            command.Parameters,
            parameter => SyntaxGrammarMatrix.IntegerParameterEquals(parameter.Value, 5L));
    }

    [Fact]
    public void Compile_FirebirdSetOffsetOnly_PreservesCanonicalOffset()
    {
        var command = CompileFirebirdSource(
            "SELECT id FROM users UNION ALL SELECT id FROM admins ORDER BY id OFFSET 5 ROWS");

        Assert.Contains("SELECT SKIP", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            command.Parameters,
            parameter => SyntaxGrammarMatrix.IntegerParameterEquals(parameter.Value, 5L));
    }

    [Fact]
    public void Compile_FirebirdMaxIntLimitAndOffset_DoesNotOverflowRowsRange()
    {
        var command = Compile(
            "SELECT id FROM users ORDER BY id LIMIT 2147483647 OFFSET 2147483647",
            SqlAgentToolType.Firebird);

        Assert.Contains("ROWS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(2147483648L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal(4294967294L, Convert.ToInt64(command.Parameters[1].Value));
    }

    private static CompiledSqlCommand CompileFirebirdSource(string sql) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Firebird),
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("native-pagination-range-v1"),
            new SqlExecutionPlanPolicy());

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("native-pagination-range-v1"),
            new SqlExecutionPlanPolicy());
}
