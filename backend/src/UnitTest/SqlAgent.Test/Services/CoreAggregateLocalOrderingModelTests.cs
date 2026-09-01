using Xunit;

namespace SqlAgent.Test.Services;

public class CoreAggregateLocalOrderingModelTests
{
    [Fact]
    public void QueryCompiler_ModeledAggregateLocalOrdering_FailsClosedBeforeLowering()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',') FROM users",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var projection = Assert.Single(select.Select);
        var aggregate = Assert.IsType<FunctionCallExpr>(projection.Expression);
        aggregate.AggregateOrderBy =
        [
            new OrderByItem(
                aggregate.Arguments[0],
                false,
                NullOrderingKind.Default,
                aggregate.Span)
        ];
        projection.Expression = aggregate;
        parsed.Statement = select;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("aggregate-local ORDER BY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryCompiler_ModeledAggregateLocalOrderingInsideScalarSubquery_FailsClosed()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT id FROM users", SqlAgentToolType.Postgres);
        var outer = Assert.IsType<SelectStatement>(parsed.Statement);

        var innerParsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',') FROM audit_log",
            SqlAgentToolType.Postgres);
        var inner = Assert.IsType<SelectStatement>(innerParsed.Statement);
        var innerProjection = Assert.Single(inner.Select);
        var aggregate = Assert.IsType<FunctionCallExpr>(innerProjection.Expression);
        aggregate.AggregateOrderBy =
        [
            new OrderByItem(
                aggregate.Arguments[0],
                false,
                NullOrderingKind.Default,
                aggregate.Span)
        ];
        innerProjection.Expression = aggregate;
        var subquery = new SubqueryExpr(inner, inner.Span);
        outer.Select[0].Expression = subquery;
        parsed.Statement = outer;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("aggregate-local ORDER BY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryCompiler_StructuredSqlServerConstantOrdering_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',') FROM users",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var projection = Assert.Single(select.Select);
        var aggregate = Assert.IsType<FunctionCallExpr>(projection.Expression);
        aggregate.AggregateOrderBy =
        [
            new OrderByItem(
                new LiteralExpr(1, aggregate.Span),
                false,
                NullOrderingKind.Default,
                aggregate.Span)
        ];
        projection.Expression = aggregate;
        parsed.Statement = select;
        parsed.EnforceSourceDialectSyntax = false;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy(),
                new SqlProviderCapabilityProfile(
                    SqlAgentToolType.MsSqlServer,
                    new Version(14, 0),
                    110)));

        Assert.Contains("non-constant", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DmlCompiler_ModeledAggregateLocalOrdering_FailsClosedBeforeLowering()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET summary = STRING_AGG(name, ',') WHERE id = 1",
            SqlAgentToolType.Postgres);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        var assignment = Assert.Single(update.Assignments);
        var aggregate = Assert.IsType<FunctionCallExpr>(assignment.Value);
        aggregate.AggregateOrderBy =
        [
            new OrderByItem(
                aggregate.Arguments[0],
                true,
                NullOrderingKind.Default,
                aggregate.Span)
        ];
        var updatedAssignment = new Assignment(
            assignment.Column,
            aggregate,
            assignment.Span);
        var updated = new UpdateStatement(
            update.Target,
            [updatedAssignment],
            update.Predicate,
            update.Span);
        updated.From = update.From;
        updated.Returning = update.Returning;
        parsed.Statement = updated;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("aggregate-local ORDER BY", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
