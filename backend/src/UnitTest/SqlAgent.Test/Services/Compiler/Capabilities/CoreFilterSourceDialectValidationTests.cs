using Xunit;

namespace SqlAgent.Test.Services;

public class CoreFilterSourceDialectValidationTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_AggregateFilter_IsRejectedForRawSourceDialectsWithoutFilterSyntax(
        SqlAgentToolType sourceDialect)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SUM(amount) FILTER (WHERE amount > 0) FROM orders",
            sourceDialect,
            SqlAgentToolType.Postgres));

        Assert.Contains("FILTER", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"source dialect {sourceDialect}", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresAggregateFilter_RemainsPortable()
    {
        var command = Compile(
            "SELECT SUM(amount) FILTER (WHERE amount > 0) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

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
