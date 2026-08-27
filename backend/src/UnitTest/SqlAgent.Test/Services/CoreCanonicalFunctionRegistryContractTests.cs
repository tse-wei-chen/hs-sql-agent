using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCanonicalFunctionRegistryContractTests
{
    [Theory]
    [InlineData("SELECT ABS(amount) FROM orders")]
    [InlineData("SELECT ROUND(amount, 2) FROM orders")]
    [InlineData("SELECT SUM(amount) FROM orders")]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY id) FROM orders")]
    public void Compile_RepresentativeDirectPortableFunctions_RemainSupported(string sql)
    {
        var command = Compile(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    [Fact]
    public void Compile_DirectPortableFunctionArity_RemainsCanonicalPlanContract()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT ROUND(amount, 1, 2) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("ROUND", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1-2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_WindowFunction_StillRequiresOver()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT LAG(amount) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("LAG", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires an OVER clause", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ScalarFunction_StillRejectsOver()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT ABS(amount) OVER (ORDER BY id) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("ABS", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not support OVER", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_AggregateRole_StillRejectsWherePlacement()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT id FROM orders WHERE SUM(amount) > 0",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains(
            "Aggregate function 'SUM'",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FilterModifier_RemainsEnabledForCanonicalAggregate()
    {
        var command = Compile(
            "SELECT SUM(amount) FILTER (WHERE amount > 0) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("FILTER", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlGroupConcatInWhere_StillUsesCanonicalAggregatePlacement()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT name FROM users WHERE GROUP_CONCAT(name) = 'x'",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL));

        Assert.Contains(
            "Aggregate function 'CORE_STRING_AGG'",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("canonical-function-registry-v1"),
            new SqlExecutionPlanPolicy());
}
