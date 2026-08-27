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
        var ordered = aggregate with
        {
            AggregateOrderBy =
            [
                new OrderByItem(
                    aggregate.Arguments[0],
                    Descending: false,
                    NullOrderingKind.Default,
                    aggregate.Span)
            ]
        };
        parsed = parsed with
        {
            Statement = select with
            {
                Select = [projection with { Expression = ordered }]
            }
        };

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
        var ordered = aggregate with
        {
            AggregateOrderBy =
            [
                new OrderByItem(
                    aggregate.Arguments[0],
                    Descending: false,
                    NullOrderingKind.Default,
                    aggregate.Span)
            ]
        };
        var orderedInner = inner with
        {
            Select = [innerProjection with { Expression = ordered }]
        };
        var subquery = new SubqueryExpr(orderedInner, orderedInner.Span);
        parsed = parsed with
        {
            Statement = outer with
            {
                Select = [new SelectItem(subquery, Alias: null, subquery.Span)]
            }
        };

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("aggregate-local ORDER BY", error.Message, StringComparison.OrdinalIgnoreCase);
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
        var ordered = aggregate with
        {
            AggregateOrderBy =
            [
                new OrderByItem(
                    aggregate.Arguments[0],
                    Descending: true,
                    NullOrderingKind.Default,
                    aggregate.Span)
            ]
        };
        parsed = parsed with
        {
            Statement = update with
            {
                Assignments = [assignment with { Value = ordered }]
            }
        };

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("aggregate-local ORDER BY", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
