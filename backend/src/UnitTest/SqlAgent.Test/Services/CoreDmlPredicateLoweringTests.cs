using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlPredicateLoweringTests
{
    [Fact]
    public void Compile_UpdatePredicate_CanonicalDatePartUsesProviderSql()
    {
        var command = Compile(
            "UPDATE orders SET status = 'archived' WHERE YEAR(created_at) = 2026",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Contains("EXTRACT(YEAR FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CORE_DATE_PART", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "archived"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 2026L) || Equals(parameter.Value, 2026));
    }

    [Fact]
    public void Compile_DeletePredicate_ArithmeticUsesClosedOperatorLowering()
    {
        var command = Compile(
            "DELETE FROM orders WHERE amount + 1 > 10",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("+", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1L) || Equals(parameter.Value, 1));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 10L) || Equals(parameter.Value, 10));
    }

    [Fact]
    public void Compile_DeletePredicate_ExistsSubqueryIsLoweredWithBindings()
    {
        var command = Compile(
            "DELETE FROM orders WHERE EXISTS (SELECT id FROM users WHERE active = TRUE)",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("EXISTS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, true));
    }

    [Fact]
    public void Compile_DeletePredicate_PostgresIntervalArithmeticRemainsStructured()
    {
        var command = Compile(
            "DELETE FROM events WHERE created_at < CURRENT_TIMESTAMP - INTERVAL '1 day'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS interval)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1 day", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "1 day"));
    }

    [Fact]
    public void Compile_DmlPredicate_NeverEmitsUnimplementedCanonicalFunctionName()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "DELETE FROM events WHERE JSON_EXTRACT(payload, '$.status') = 'ready'",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Oracle));

        Assert.DoesNotContain("CORE_JSON_EXTRACT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON_EXTRACT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE RANDOM() < 0.5", SqlAgentToolType.Postgres)]
    [InlineData("DELETE FROM orders WHERE RAND() < 0.5", SqlAgentToolType.MySQL)]
    [InlineData("UPDATE orders SET status = 'x' WHERE RAND() < 0.5", SqlAgentToolType.MsSqlServer)]
    public void Compile_DmlPredicate_RandomFunctionFailsBeforeMutation(
        string sql,
        SqlAgentToolType provider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(sql, provider, provider));

        Assert.Contains("Nondeterministic function", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved row set", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RandomFunction_ToOracle_FailsAtNormalizationInsteadOfEmittingRand()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "DELETE FROM orders WHERE RANDOM() < 0.5",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle));

        Assert.Contains("Random", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not translated across dialects", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());
}
