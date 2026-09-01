using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeTargetCapabilityMatrixTests
{
    private sealed record TargetMutationShape(
        string Name,
        string Sql);

    private sealed record TargetMutationFamily(
        string Name,
        SqlAgentToolType SourceDialect,
        SqlAgentToolType TargetDialect,
        IReadOnlyList<GrammarVariant<TargetMutationShape>> Shapes,
        string DiagnosticCode,
        SqlDiagnosticStage DiagnosticStage,
        SqlDiagnosticCategory DiagnosticCategory,
        string[] MessageFragments);

    private static readonly GrammarVariant<TargetMutationShape>[] JoinUsingShapes =
    [
        new(
            "root",
            new(
                "root",
                "SELECT id FROM users JOIN orders USING (id)")),
        new(
            "cte-body",
            new(
                "cte-body",
                "WITH x AS (SELECT id FROM users JOIN orders USING (id)) SELECT id FROM x")),
        new(
            "nested-subquery",
            new(
                "nested-subquery",
                "SELECT a.id FROM alpha a WHERE EXISTS (SELECT id FROM users JOIN orders USING (id))"))
    ];

    private static readonly GrammarVariant<TargetMutationShape>[] IntersectAllShapes =
    [
        new(
            "root",
            new(
                "root",
                "SELECT id FROM alpha INTERSECT ALL SELECT id FROM beta")),
        new(
            "cte-body",
            new(
                "cte-body",
                "WITH x AS (SELECT id FROM alpha INTERSECT ALL SELECT id FROM beta) SELECT id FROM x")),
        new(
            "nested-subquery",
            new(
                "nested-subquery",
                "SELECT id FROM users WHERE id IN (SELECT id FROM alpha INTERSECT ALL SELECT id FROM beta)"))
    ];

    private static readonly GrammarVariant<TargetMutationShape>[] ExceptAllShapes =
    [
        new(
            "root",
            new(
                "root",
                "SELECT id FROM alpha EXCEPT ALL SELECT id FROM beta")),
        new(
            "cte-body",
            new(
                "cte-body",
                "WITH x AS (SELECT id FROM alpha EXCEPT ALL SELECT id FROM beta) SELECT id FROM x")),
        new(
            "nested-subquery",
            new(
                "nested-subquery",
                "SELECT id FROM users WHERE id IN (SELECT id FROM alpha EXCEPT ALL SELECT id FROM beta)"))
    ];

    private static readonly GrammarVariant<TargetMutationShape>[] DistinctOnShapes =
    [
        new(
            "root",
            new(
                "root",
                "SELECT DISTINCT ON (customer_id) customer_id, created_at FROM orders ORDER BY customer_id, created_at DESC")),
        new(
            "cte-body",
            new(
                "cte-body",
                "WITH x AS (SELECT DISTINCT ON (customer_id) customer_id, created_at FROM orders ORDER BY customer_id, created_at DESC) SELECT customer_id FROM x"))
    ];

    private static readonly GrammarVariant<TargetMutationShape>[] FetchWithTiesShapes =
    [
        new(
            "root",
            new(
                "root",
                "SELECT id FROM users ORDER BY id FETCH FIRST 10 ROWS WITH TIES")),
        new(
            "cte-body",
            new(
                "cte-body",
                "WITH x AS (SELECT id FROM users ORDER BY id FETCH FIRST 10 ROWS WITH TIES) SELECT id FROM x"))
    ];

    private static readonly GrammarVariant<TargetMutationShape>[] LateralShapes =
    [
        new(
            "root",
            new(
                "root",
                "SELECT q.id FROM LATERAL (SELECT id FROM users) q")),
        new(
            "cte-body",
            new(
                "cte-body",
                "WITH x AS (SELECT q.id FROM LATERAL (SELECT id FROM users) q) SELECT id FROM x"))
    ];

    private static readonly GrammarVariant<TargetMutationShape>[] MySqlDateShapes =
    [
        new(
            "projection",
            new(
                "projection",
                "SELECT DATE(created_at) FROM events")),
        new(
            "predicate",
            new(
                "predicate",
                "SELECT id FROM events WHERE DATE(created_at) = DATE(completed_at)")),
        new(
            "cte-body",
            new(
                "cte-body",
                "WITH x AS (SELECT DATE(created_at) AS d FROM events) SELECT d FROM x"))
    ];

    private static readonly TargetMutationFamily[] Families =
    [
        new(
            "pg-join-using-to-sqlserver",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            JoinUsingShapes,
            "SQL_TARGET_CAPABILITY_REJECTED",
            SqlDiagnosticStage.TargetCapability,
            SqlDiagnosticCategory.Capability,
            ["join.using", "MsSqlServer"]),
        new(
            "pg-intersect-all-to-mysql",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            IntersectAllShapes,
            "SQL_TARGET_CAPABILITY_REJECTED",
            SqlDiagnosticStage.TargetCapability,
            SqlDiagnosticCategory.Capability,
            ["set.intersect_all", "MySQL"]),
        new(
            "pg-except-all-to-sqlserver",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            ExceptAllShapes,
            "SQL_TARGET_CAPABILITY_REJECTED",
            SqlDiagnosticStage.TargetCapability,
            SqlDiagnosticCategory.Capability,
            ["set.except_all", "MsSqlServer"]),
        new(
            "pg-distinct-on-to-mysql",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            DistinctOnShapes,
            "SQL_TARGET_CAPABILITY_REJECTED",
            SqlDiagnosticStage.TargetCapability,
            SqlDiagnosticCategory.Capability,
            ["select.distinct_on", "MySQL"]),
        new(
            "pg-fetch-with-ties-to-mysql",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            FetchWithTiesShapes,
            "SQL_SEMANTIC_VALIDATION_FAILED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Semantic,
            ["select.fetch_with_ties", "MySQL"]),
        new(
            "pg-lateral-to-mysql",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            LateralShapes,
            "SQL_SEMANTIC_VALIDATION_FAILED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Semantic,
            ["select.lateral_derived", "MySQL"]),
        new(
            "mysql-date-to-postgres",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres,
            MySqlDateShapes,
            "SQL_SEMANTIC_VALIDATION_FAILED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Semantic,
            ["temporal.date_only", "Cross-dialect lowering"])
    ];

    public static IEnumerable<object[]> NegativeTargetCapabilityMatrix()
    {
        foreach (var family in Families)
        foreach (var shape in family.Shapes)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    family.Name,
                    shape.Name),
                family.SourceDialect,
                family.TargetDialect,
                shape.Value.Sql,
                family.DiagnosticCode,
                family.DiagnosticStage,
                family.DiagnosticCategory,
                family.MessageFragments
            ];
        }
    }

    [Fact]
    public void NegativeTargetCapabilityMatrix_IsCombinatorialAndCollisionFree()
    {
        var cases = NegativeTargetCapabilityMatrix().ToArray();
        var expectedCount = Families.Sum(family => family.Shapes.Count);

        Assert.Equal(18, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[3]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(NegativeTargetCapabilityMatrix))]
    public void NegativeTargetCapabilityMatrix_NativeSucceedsButUnsupportedTargetFailsClosed(
        string name,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect,
        string sql,
        string expectedDiagnosticCode,
        SqlDiagnosticStage expectedDiagnosticStage,
        SqlDiagnosticCategory expectedDiagnosticCategory,
        string[] expectedMessageFragments)
    {
        var native = Compile(
            sql,
            sourceDialect,
            sourceDialect);

        Assert.False(string.IsNullOrWhiteSpace(native.Sql), name);

        var error = Record.Exception(
            () => Compile(
                sql,
                sourceDialect,
                targetDialect));

        Assert.NotNull(error);
        Assert.Equal(typeof(SqlCompilationException), error.GetType());

        foreach (var fragment in expectedMessageFragments)
        {
            Assert.Contains(
                fragment,
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);

        Assert.Equal(expectedDiagnosticCode, diagnostic.Code);
        Assert.Equal(expectedDiagnosticStage, diagnostic.Stage);
        Assert.Equal(expectedDiagnosticCategory, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length >= 0, name);
        Assert.True(diagnostic.Span.End <= sql.Length, name);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                sql,
                sourceDialect),
            targetDialect,
            new SqlPlanValidationContext(
                "negative-target-capability-matrix-v1"),
            new SqlExecutionPlanPolicy());
}
