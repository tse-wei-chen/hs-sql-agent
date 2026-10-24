using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlSemanticValidationTests
{
    [Theory]
    [InlineData("SELECT id FROM orders WHERE SUM(amount) > 10", "Aggregate function 'SUM'")]
    [InlineData("SELECT id FROM orders GROUP BY SUM(amount)", "Aggregate function 'SUM'")]
    [InlineData("SELECT id FROM orders WHERE ROW_NUMBER() OVER (ORDER BY id) = 1", "Window expressions")]
    [InlineData("SELECT SUM(AVG(amount)) FROM orders", "cannot be nested")]
    public void Compile_InvalidAggregateOrWindowPlacement_FailsClosed(string sql, string expectedMessage)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(sql, SqlAgentToolType.Postgres));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_AggregateInHaving_RemainsSupported()
    {
        var command = Compile(
            "SELECT customer_id, SUM(amount) AS total FROM orders GROUP BY customer_id HAVING SUM(amount) > 10",
            SqlAgentToolType.Postgres);

        Assert.Contains("SUM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAVING", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_AggregateInWindowSpecification_ForNonPostgresTarget_RemainsRejected(
        SqlAgentToolType targetProvider)
    {
        const string sql =
            "SELECT customer_id, ship_country, " +
            "ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY SUM(net_order_value) DESC) AS country_rank " +
            "FROM order_value GROUP BY customer_id, ship_country";

        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, SqlAgentToolType.Postgres, targetProvider));

        Assert.Contains("window specification", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aggregate function 'SUM'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FullJoinForMySqlTarget_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT a.id FROM a FULL JOIN b ON a.id = b.id",
            SqlAgentToolType.MySQL));

        Assert.Contains("join.full", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationWithKnownMismatchedProjectionWidths_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT id FROM users UNION SELECT id, name FROM archived_users",
            SqlAgentToolType.Postgres));

        Assert.Contains("projection width", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> SixDialects() =>
        Enum.GetValues<SqlAgentToolType>()
            .Select(provider => new object[] { provider });

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void Compile_MixedScalarSubquerySemanticGraph_CompilesWithStableParameterBinding(
        SqlAgentToolType provider)
    {
        const int depth = 24;
        var sql = "SELECT "
            + string.Concat(Enumerable.Repeat("(SELECT ", depth))
            + "id FROM users WHERE id = 7"
            + new string(')', depth);

        var result = Compile(sql, provider);

        Assert.Equal(depth + 1,
            result.Sql.Split("SELECT ", StringSplitOptions.None).Length - 1);
        Assert.Single(result.Parameters);
        Assert.Equal(7, Convert.ToInt32(result.Parameters[0].Value));
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void Compile_NestedSetBranchWidthMismatch_RemainsFailClosed(
        SqlAgentToolType provider)
    {
        var sql = "SELECT (SELECT id FROM users "
            + "UNION ALL SELECT id, name FROM archived_users) FROM users";

        var error = Assert.Throws<SqlCompilationException>(() => Compile(sql, provider));

        Assert.Contains("projection width", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InnerAggregateInWhere_StillRejectedAcrossScalarSubquery()
    {
        var sql = "SELECT (SELECT id FROM orders WHERE SUM(amount) > 10) FROM users";
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, SqlAgentToolType.Postgres));

        Assert.Contains("Aggregate function 'SUM'", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE/ON/FILTER", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationWithMatchingProjectionWidths_RemainsSupported()
    {
        var command = Compile(
            "SELECT id FROM users UNION SELECT id FROM archived_users",
            SqlAgentToolType.Postgres);

        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType provider) =>
        Compile(sql, provider, provider);

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
