using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CorePostgresStringAggregateOrderingTests
{
    [Fact]
    public void Parse_PostgresStringAggOrdering_IsStructured()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',' ORDER BY created_at DESC, name ASC NULLS LAST) FROM users",
            SqlAgentToolType.Postgres);
        var function = Assert.IsType<FunctionCallExpr>(
            Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);

        Assert.Equal(2, function.Arguments.Length);
        Assert.Equal(2, function.AggregateOrderBy.Length);
        Assert.Equal(AggregateOrderSyntaxKind.Inline, function.AggregateOrderSyntax);
        Assert.True(function.AggregateOrderBy[0].Descending);
        Assert.Equal(NullOrderingKind.Last, function.AggregateOrderBy[1].NullOrdering);
    }

    [Fact]
    public void Compile_PostgresStringAggOrdering_PreservesStructuredOrdering()
    {
        var command = Compile(
            "SELECT STRING_AGG(name, ',' ORDER BY created_at DESC, name ASC NULLS LAST) FROM users",
            SqlAgentToolType.Postgres);

        Assert.Contains("STRING_AGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresStringAggSeparator_WithBackslashQuoteAndUnicode_IsBound()
    {
        var command = Compile(
            "SELECT STRING_AGG(name, '\\''雪') FROM users",
            SqlAgentToolType.Postgres);

        Assert.DoesNotContain("\\''雪", command.Sql, StringComparison.Ordinal);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal("\\'雪", parameter.Value);
    }

    [Fact]
    public void Compile_PostgresStringAggOrdering_BindsNestedOrderExpressionParameters()
    {
        var command = Compile(
            "SELECT STRING_AGG(name, ',' ORDER BY COALESCE(sort_key, 'fallback') DESC) FROM users",
            SqlAgentToolType.Postgres);

        Assert.Contains("COALESCE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "fallback"));
    }

    [Fact]
    public void Compile_PostgresStringAggOrdering_ToNonPostgresTarget_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT STRING_AGG(name, ',' ORDER BY created_at) FROM users",
            SqlAgentToolType.Firebird));

        Assert.Contains("aggregate.string.ordering", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Firebird", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresStringAggDistinctOrdering_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT STRING_AGG(DISTINCT name, ',' ORDER BY name) FROM users",
            SqlAgentToolType.Postgres));

        Assert.Contains("DISTINCT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresStringAggMissingSeparator_IsRejectedAtSourceBoundary()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT STRING_AGG(name ORDER BY created_at) FROM users",
            SqlAgentToolType.Postgres));

        Assert.Contains("STRING_AGG", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two-argument", error.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Compile_StructuredOrderedStringAggregate_IsNotBoundToRawSourceDialect()
    {
        var parsedSource = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',' ORDER BY created_at) FROM users",
            SqlAgentToolType.Postgres);
        var parsed = new ParsedStatement(
            parsedSource.Statement,
            SqlAgentToolType.MySQL,
            false);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("STRING_AGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_EnablesOrderedStringAggregationOnlyForPostgresTarget()
    {
        var postgres = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        Assert.Equal(SqlCapabilityStatus.Supported, postgres.Status);

        var mysql = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MySQL).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        Assert.Equal(SqlCapabilityStatus.Supported, mysql.Status);

        foreach (var provider in new[]
                 {
                     SqlAgentToolType.Sqlite,
                     SqlAgentToolType.MsSqlServer,
                     SqlAgentToolType.Oracle,
                     SqlAgentToolType.Firebird
                 })
        {
            var capability = Assert.Single(
                SqlCapabilityMatrix.ForProvider(provider).Capabilities,
                item => item.Id == "aggregate.string.ordering");
            Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        }
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
