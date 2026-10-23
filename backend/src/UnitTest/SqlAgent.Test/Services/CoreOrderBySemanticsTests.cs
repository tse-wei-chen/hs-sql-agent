using Xunit;

namespace SqlAgent.Test.Services;

public class CoreOrderBySemanticsTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_BareIntegerOrderBy_RendersOutputOrdinalWithoutBinding(SqlAgentToolType provider)
    {
        var command = Compile("SELECT id, name FROM users ORDER BY 2 DESC", provider);

        Assert.Contains("ORDER BY 2 DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void Compile_OrderByOrdinalBeyondKnownProjectionWidth_IsRejected()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT id FROM users ORDER BY 2", SqlAgentToolType.Postgres));

        Assert.Contains("projection width 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OrderByZeroOrdinal_IsRejected()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT id FROM users ORDER BY 0", SqlAgentToolType.Postgres));

        Assert.Contains("must be positive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OrderByOrdinal_WithWildcardProjection_RemainsSupported()
    {
        var command = Compile("SELECT * FROM users ORDER BY 2", SqlAgentToolType.Postgres);

        Assert.Contains("ORDER BY 2", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void Compile_SetOperationOrderByOrdinal_RendersCombinedOutputPosition()
    {
        var command = Compile(
            "SELECT id FROM users UNION SELECT id FROM admins ORDER BY 1",
            SqlAgentToolType.Postgres);

        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY 1", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(command.Parameters);
    }

    public static IEnumerable<object[]> SetOperationDialects() =>
        Enum.GetValues<SqlAgentToolType>().Select(provider => new object[] { provider });

    [Theory]
    [MemberData(nameof(SetOperationDialects))]
    public void Compile_MultiBranchSetOperation_PreservesBranchAndParameterOrder(
        SqlAgentToolType provider)
    {
        const int branchCount = 12;
        var sql = string.Join(" UNION ALL ",
            Enumerable.Range(1, branchCount).Select(value =>
                $"SELECT id FROM users WHERE id = {value}")) + " ORDER BY 1";

        var command = Compile(sql, provider);

        Assert.Equal(branchCount - 1,
            command.Sql.Split("UNION ALL", StringSplitOptions.None).Length - 1);
        Assert.Contains("ORDER BY 1", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(branchCount, command.Parameters.Length);
        Assert.Equal(
            Enumerable.Range(1, branchCount).Select(value => (long)value),
            command.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
    }

    [Fact]
    public void Compile_SetOperationOrderByProjectedAlias_IsSupported()
    {
        var command = Compile(
            "SELECT id AS key FROM users UNION SELECT id FROM admins ORDER BY key",
            SqlAgentToolType.Postgres);

        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationOrderByExpressionOverProjectedOutput_IsSupported()
    {
        var command = Compile(
            "SELECT name FROM users UNION SELECT name FROM admins ORDER BY LOWER(name)",
            SqlAgentToolType.Postgres);

        Assert.Contains("LOWER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationOrderByBranchQualifiedColumn_IsRejected()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT id FROM users UNION SELECT id FROM admins ORDER BY users.id",
                SqlAgentToolType.Postgres));

        Assert.Contains("combined output", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationOrderByUnprojectedColumn_IsRejected()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT id FROM users UNION SELECT id FROM admins ORDER BY name",
                SqlAgentToolType.Postgres));

        Assert.Contains("not present", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DecimalOrderByLiteral_IsNotTreatedAsOutputOrdinal()
    {
        var command = Compile("SELECT id FROM users ORDER BY 1.0", SqlAgentToolType.Postgres);

        Assert.DoesNotContain("ORDER BY 1.0", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
    }

    [Fact]
    public void Compile_WindowOrderByInteger_RemainsScalarWindowExpression()
    {
        var command = Compile(
            "SELECT ROW_NUMBER() OVER (ORDER BY 1) AS rn FROM users",
            SqlAgentToolType.Postgres);

        Assert.Contains("OVER (ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(1, command.Parameters[0].Value);
    }

    [Fact]
    public void Compile_SqlServerOffsetNamedProjection_RemainsStableThroughStructuralSpans()
    {
        var command = Compile(
            "SELECT id FROM users ORDER BY id OFFSET 5 ROWS",
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("ROW_NUMBER() OVER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, provider);
        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            provider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
    }
}
