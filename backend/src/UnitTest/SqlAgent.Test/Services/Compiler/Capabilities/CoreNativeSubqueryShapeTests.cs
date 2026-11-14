using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreNativeSubqueryShapeTests
{
    [Fact]
    public void Compile_ScalarSubqueryWithOneColumn_IsRendered()
    {
        var command = Compile(
            "SELECT (SELECT MAX(id) FROM users) AS max_id FROM orders");

        Assert.Contains("(SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ScalarSubqueryWithMultipleColumns_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT (SELECT id, name FROM users) AS value FROM orders"));

        Assert.Contains("Scalar subquery", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InSubqueryWithMultipleColumns_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT id FROM orders WHERE id IN (SELECT id, tenant_id FROM users)"));

        Assert.Contains("Scalar subquery", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ExistsSubqueryWithMultipleColumns_RemainsValid()
    {
        var command = Compile(
            "SELECT id FROM orders WHERE EXISTS (SELECT id, tenant_id FROM users)");

        Assert.Contains("EXISTS (SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ScalarWildcardSubquery_FailsClosedWhenWidthIsUnknown()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT (SELECT * FROM users) AS value FROM orders"));

        Assert.Contains("statically known", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MalformedBinaryInWithoutSubqueryRhs_FailsClosed()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM orders WHERE id IN (SELECT id FROM users)",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var predicate = Assert.IsType<BinaryExpr>(select.Where);
        var malformedPredicate = new BinaryExpr(
            predicate.Left,
            predicate.Operator,
            new LiteralExpr(7, SourceSpan.Unknown),
            predicate.Span,
            predicate.LikeEscape);
        var malformedSelect = new SelectStatement(
            select.Ctes,
            select.Distinct,
            select.Select,
            select.From,
            select.Joins,
            malformedPredicate,
            select.GroupBy,
            select.Having,
            select.OrderBy,
            select.Limit,
            select.Offset,
            select.Span);
        parsed.Statement = malformedSelect;
        parsed.EnforceSourceDialectSyntax = false;
        var malformed = parsed;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                malformed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("native-subquery-shape-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("binary IN/NOT IN", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subquery RHS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(string sql) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("native-subquery-shape-v1"),
            new SqlExecutionPlanPolicy());
}
