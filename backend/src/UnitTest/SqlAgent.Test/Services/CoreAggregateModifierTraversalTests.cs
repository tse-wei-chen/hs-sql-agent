using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreAggregateModifierTraversalTests
{
    [Fact]
    public void Compile_MySqlSourceProfile_RewritesConcatInsideAggregateOrdering()
    {
        var sourceProfile = MySqlProfile("PIPES_AS_CONCAT");
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT GROUP_CONCAT(name ORDER BY first_name || last_name) FROM users",
            SqlAgentToolType.MySQL,
            sourceProfile);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("aggregate-traversal-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServer14Target_RewritesConcatInsideAggregateOrdering()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',' ORDER BY first_name || last_name) FROM users",
            SqlAgentToolType.Postgres);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("aggregate-traversal-v1"),
            new SqlExecutionPlanPolicy(),
            SqlServerProfile(14, 140));

        Assert.Contains("WITHIN GROUP", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" + ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ASC NULLS FIRST", false)]
    [InlineData("ASC NULLS LAST", true)]
    public void Compile_MySqlTarget_RewritesNullOrderingInsideAggregateOrdering(
        string ordering,
        bool expectsNullRank)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            $"SELECT STRING_AGG(name, ',' ORDER BY created_at {ordering}) FROM users",
            SqlAgentToolType.Postgres);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("aggregate-traversal-v1"),
            new SqlExecutionPlanPolicy());

        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            expectsNullRank,
            command.Sql.Contains("CASE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compile_AggregateOrderingScalarCte_FailsClosedOnSqlServer()
    {
        var parsed = WithAggregateOrderExpression(
            new SubqueryExpr(
                CoreSqlTextParser.ParseQuery(
                    "WITH recent(id) AS (SELECT id FROM archived) SELECT MAX(id) FROM recent",
                    SqlAgentToolType.Postgres).Statement,
                SourceSpan.Unknown));

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("aggregate-traversal-v1"),
                new SqlExecutionPlanPolicy(),
                SqlServerProfile(14, 140)));

        Assert.Contains("select.cte_scope", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scalar/EXISTS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_AggregateOrderingScalarCte_CanonicalizesColumnAliasesOnPostgres()
    {
        var parsed = WithAggregateOrderExpression(
            new SubqueryExpr(
                CoreSqlTextParser.ParseQuery(
                    "WITH recent(id) AS (SELECT id FROM archived) SELECT MAX(id) FROM recent",
                    SqlAgentToolType.Postgres).Statement,
                SourceSpan.Unknown));

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("aggregate-traversal-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AS ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recent(id)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NoFromAggregateOrderingColumn_FailsAtCoreBoundary()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT STRING_AGG('x', ',' ORDER BY missing_column)",
                    SqlAgentToolType.Postgres),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("aggregate-traversal-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("missing_column", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires a FROM source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_AggregateOrderingScalarSubquery_ValidatesNestedNoFromReferences()
    {
        var parsed = WithAggregateOrderExpression(
            new SubqueryExpr(
                CoreSqlTextParser.ParseQuery(
                    "SELECT missing_column",
                    SqlAgentToolType.Postgres).Statement,
                SourceSpan.Unknown));

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("aggregate-traversal-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("missing_column", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires a FROM source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NestedAggregateInsideAggregateOrdering_FailsSemanticValidation()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT STRING_AGG(name, ',' ORDER BY SUM(score)) FROM users",
                    SqlAgentToolType.Postgres),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("aggregate-traversal-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("cannot be nested", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertValuesAggregateOrderingColumn_FailsScopeValidation()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseDml(
                    "INSERT INTO audit_log (summary) VALUES " +
                    "(STRING_AGG('x', ',' ORDER BY source_column))",
                    SqlAgentToolType.Postgres),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("aggregate-traversal-v1"),
                new DmlCompilationPolicy()));

        Assert.Contains("INSERT VALUES scalar expression", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source_column", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedStatement WithAggregateOrderExpression(SqlExpr orderExpression)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',') FROM users",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var projection = Assert.Single(select.Select);
        var function = Assert.IsType<FunctionCallExpr>(projection.Expression);
        var ordered = function with
        {
            AggregateOrderBy =
            [
                new OrderByItem(
                    orderExpression,
                    Descending: false,
                    NullOrderingKind.Default,
                    SourceSpan.Unknown)
            ]
        };

        return parsed with
        {
            Statement = select with
            {
                Select = [projection with { Expression = ordered }]
            },
            EnforceSourceDialectSyntax = false
        };
    }

    private static SqlProviderCapabilityProfile MySqlProfile(params string[] modes) =>
        new(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(modes, StringComparer.OrdinalIgnoreCase));

    private static SqlProviderCapabilityProfile SqlServerProfile(
        int major,
        int compatibility) =>
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(major, 0),
            CompatibilityLevel: compatibility);
}
