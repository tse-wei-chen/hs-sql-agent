using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreWindowCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Matrix_WindowCapabilities_RemainTranslated(
        SqlAgentToolType provider)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);

        Assert.Equal(
            SqlCapabilityStatus.Translated,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "window.basic").Status);
        Assert.Equal(
            SqlCapabilityStatus.Translated,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "window.frame").Status);
    }

    [Fact]
    public void Compile_NthValueForSqlServer_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT NTH_VALUE(amount, 1) OVER (ORDER BY id) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains(
            "function.nth_value",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_FrameInsensitiveWindowFunction_RejectedTargets_RemainFailClosed(
        SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT ROW_NUMBER() OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM orders",
            SqlAgentToolType.Postgres,
            targetProvider));

        Assert.Contains(
            "window.frame.row_number",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerModeledWindowFunction_StillRequiresOrderBy()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT ROW_NUMBER() OVER () FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains(
            "window.order_by",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_FrameSensitiveWindowFunction_WithFrame_RemainsAccepted(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "SELECT FIRST_VALUE(amount) OVER (" +
            "ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM orders",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    [Fact]
    public void Compile_SqlServerRangeOffset_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SUM(amount) OVER (ORDER BY id RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains(
            "window.range_offset",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LAG", SqlAgentToolType.MsSqlServer, "function.lag.negative_offset")]
    [InlineData("LEAD", SqlAgentToolType.MsSqlServer, "function.lead.negative_offset")]
    [InlineData("LAG", SqlAgentToolType.MySQL, "function.lag.negative_offset")]
    [InlineData("LEAD", SqlAgentToolType.MySQL, "function.lead.negative_offset")]
    public void Compile_NegativeLagLeadOffset_RejectedTargets_RemainFailClosed(
        string functionName,
        SqlAgentToolType targetProvider,
        string capability)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            $"SELECT {functionName}(amount, -1) OVER (ORDER BY id) FROM orders",
            SqlAgentToolType.Postgres,
            targetProvider));

        Assert.Contains(
            capability,
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerWindowedAggregate_WithoutOrderBy_RemainsAccepted()
    {
        var command = Compile(
            "SELECT SUM(amount) OVER () FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("window-capability-contract-v1"),
            new SqlExecutionPlanPolicy());
}
