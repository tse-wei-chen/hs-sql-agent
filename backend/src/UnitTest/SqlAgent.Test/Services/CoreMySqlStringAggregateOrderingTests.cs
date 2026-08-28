using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreMySqlStringAggregateOrderingTests
{
    [Fact]
    public void Parse_MySqlGroupConcatOrderingAndSeparator_AreStructured()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT GROUP_CONCAT(name ORDER BY created_at DESC, name ASC SEPARATOR '|') FROM users",
            SqlAgentToolType.MySQL);
        var function = Assert.IsType<FunctionCallExpr>(
            Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);

        Assert.Single(function.Arguments);
        Assert.Equal(2, function.AggregateOrderBy.Length);
        Assert.Equal(AggregateOrderSyntaxKind.Inline, function.AggregateOrderSyntax);
        Assert.Equal("|", function.AggregateSeparatorClause);
        Assert.True(function.AggregateOrderBy[0].Descending);
    }

    [Fact]
    public void Compile_MySqlOrderedGroupConcat_UsesNativeOrderAndSeparatorClauses()
    {
        var command = CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY created_at DESC, name ASC SEPARATOR '|') FROM users");

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR '|'", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(", '|')", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MySqlOrderedGroupConcat_DefaultSeparator_IsComma()
    {
        var command = CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY created_at) FROM users");

        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR ','", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MySqlRawSeparatorWithoutOrdering_IsCanonicalized()
    {
        var command = CompileRaw(
            "SELECT GROUP_CONCAT(name SEPARATOR 'a''b,|') FROM users");

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR 'a''b,|'", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(", 'a''b,|')", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MySqlOrdering_PreservesNestedExpressionBindings()
    {
        var command = CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY COALESCE(sort_key, 'fallback') DESC SEPARATOR '|') FROM users");

        Assert.Contains("COALESCE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "fallback"));
    }

    [Fact]
    public void Compile_MySqlMultiExpressionGroupConcat_WithOrderingStillFailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT GROUP_CONCAT(first_name, last_name ORDER BY created_at SEPARATOR '|') FROM users"));

        Assert.Contains("multiple value expressions", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one value expression", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlSeparator_RequiresStringLiteral()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT GROUP_CONCAT(name SEPARATOR separator_column) FROM users",
                SqlAgentToolType.MySQL));

        Assert.Contains("string literal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NonMySqlRawSeparatorClause_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT STRING_AGG(name, ',' SEPARATOR '|') FROM users",
                    SqlAgentToolType.Postgres),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("SEPARATOR", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL GROUP_CONCAT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqliteGroupConcatSeparatorClause_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT GROUP_CONCAT(name SEPARATOR '|') FROM users",
                    SqlAgentToolType.Sqlite),
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("SEPARATOR", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL GROUP_CONCAT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StructuredOrdering_ToMySql_DoesNotRequireSourceProfile()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, '|' ORDER BY created_at DESC) FROM users",
            SqlAgentToolType.Postgres) with
        {
            SourceDialect = SqlAgentToolType.Oracle,
            EnforceSourceDialectSyntax = false,
            SourceProfile = null
        };

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR '|'", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Matrix_MySqlOrdering_IsSupportedWithoutRuntimeProfile()
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MySQL).Capabilities,
            item => item.Id == "aggregate.string.ordering");

        Assert.Equal(SqlCapabilityStatus.Supported, capability.Status);
        Assert.Contains("GROUP_CONCAT", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileRaw(string sql) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL),
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
