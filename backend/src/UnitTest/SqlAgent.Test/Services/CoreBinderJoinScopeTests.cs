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


    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_ScalarProjectionHeapChain_PreservesInnermostBinding(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT (SELECT (SELECT 13))";
        var command = Compile(sql, dialect);
        Assert.Single(command.Parameters);
        Assert.Equal(13L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void Compile_ComplexScalarProjection_RetainsExpressionAndAliasGrammar()
    {
        var expression = Compile("SELECT (SELECT 5) + 7", SqlAgentToolType.Postgres);
        Assert.Equal(new long[] { 5, 7 },
            expression.Parameters.Select(p => Convert.ToInt64(p.Value)));
        var alias = Compile("SELECT (SELECT 5) AS projected", SqlAgentToolType.Postgres);
        Assert.Contains("projected", alias.Sql, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_StreamingSearchedCaseScalarQuery_PreservesParameterOrder(
        SqlAgentToolType provider)
    {
        var sql = "SELECT CASE WHEN 2 = 3 THEN ("
            + "SELECT CASE WHEN 4 = 5 THEN (SELECT 6) ELSE 7 END"
            + ") ELSE 8 END";
        var command = Compile(sql, provider);
        Assert.Equal(new long[] { 2, 3, 4, 5, 6, 7, 8 },
            command.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Contains("CASE WHEN", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ComplexSearchedCaseProjection_PreservesExpressionAndAliasParsing()
    {
        var expression = Compile(
            "SELECT CASE WHEN 1 = 1 THEN (SELECT 9) ELSE 0 END + 2",
            SqlAgentToolType.Postgres);
        Assert.Equal(new long[] { 1, 1, 9, 0, 2 },
            expression.Parameters.Select(p => Convert.ToInt64(p.Value)));

        var alias = Compile(
            "SELECT CASE WHEN 1 = 1 THEN (SELECT 9) ELSE 0 END AS selected_value",
            SqlAgentToolType.Postgres);
        Assert.Contains("selected_value", alias.Sql, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Compile_PrefixedJoinedLateral_PreservesLexicalParameterOrder()
    {
        var sql = "SELECT 11 FROM users u "
            + "JOIN users v ON v.id = 22 "
            + "JOIN LATERAL (SELECT v.id AS id WHERE v.id = 33) l ON l.id = 44 "
            + "WHERE u.id = 55";
        var compiled = Compile(sql, SqlAgentToolType.Postgres);
        Assert.Equal(new long[] { 11, 22, 33, 44, 55 },
            compiled.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Contains("LATERAL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Compile_TwoDependentLaterals_PreserveLexicalParameterOrder()
    {
        var sql = "SELECT 11 FROM users u "
            + "JOIN LATERAL (SELECT 22 AS id WHERE u.id = 33) a ON a.id = 44 "
            + "JOIN LATERAL (SELECT 55 AS id WHERE a.id = 66) b ON b.id = 77 "
            + "WHERE u.id = 88";
        var command = Compile(sql, SqlAgentToolType.Postgres);
        Assert.Equal(new long[] { 11, 22, 33, 44, 55, 66, 77, 88 },
            command.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Equal(2, command.Sql.Split("LATERAL (", StringSplitOptions.None).Length - 1);
    }


    [Fact]
    public void Compile_ThreeDependentLaterals_PreserveProjectionAndJoinParameterOrder()
    {
        var sql = "SELECT 11 FROM users u "
            + "JOIN LATERAL (SELECT 22 AS id WHERE u.id = 33) a ON a.id = 44 "
            + "JOIN LATERAL (SELECT 55 AS id WHERE a.id = 66) b ON b.id = 77 "
            + "JOIN LATERAL (SELECT 88 AS id WHERE b.id = 99) c ON c.id = 100 "
            + "WHERE u.id = 111";
        var command = Compile(sql, SqlAgentToolType.Postgres);
        Assert.Equal(new long[] { 11, 22, 33, 44, 55, 66, 77, 88, 99, 100, 111 },
            command.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Equal(3, command.Sql.Split("LATERAL (", StringSplitOptions.None).Length - 1);
    }


    [Fact]
    public void Compile_DeepSiblingLaterals_PreservesLeftToRightParameterOrder()
    {
        var left = "SELECT 11 AS id WHERE u.id = 22";
        var right = "SELECT 33 AS id WHERE a.id = 44";
        for (var i = 0; i < 48; i++)
        {
            left = "SELECT x.id AS id FROM LATERAL (" + left + ") x";
            right = "SELECT x.id AS id FROM LATERAL (" + right + ") x";
        }
        var sql = "SELECT 5 FROM users u "
            + "JOIN LATERAL (" + left + ") a ON a.id = 55 "
            + "JOIN LATERAL (" + right + ") b ON b.id = 66 WHERE u.id = 77";
        var compiled = Compile(sql, SqlAgentToolType.Postgres);
        Assert.Equal(new long[] { 5, 11, 22, 55, 33, 44, 66, 77 },
            compiled.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType provider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, provider),
            provider,
            new SqlPlanValidationContext("binder-join-scope-iterative-v1"),
            new SqlExecutionPlanPolicy());
}
