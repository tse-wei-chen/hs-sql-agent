using Xunit;

namespace SqlAgent.Test.Services;

public sealed class AdvancedJsonPathGrammarMatrixTests
{
    public const int ExpectedPositiveCaseCount = 18;
    public const int ExpectedNegativeCaseCount = 21;

    private static readonly SqlAgentToolType[] JsonExtractSources =
    [
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Sqlite
    ];

    private static readonly SqlAgentToolType[] JsonExtractTargets =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Sqlite
    ];

    private static readonly string[] ProjectionContexts =
    [
        "root",
        "cte",
        "scalar-subquery"
    ];

    public static IEnumerable<object[]> PositiveMatrix()
    {
        foreach (var source in JsonExtractSources)
        foreach (var target in JsonExtractTargets)
        foreach (var context in ProjectionContexts)
            yield return
            [
                $"{source}__{target}__array-index__{context}",
                source,
                target,
                Query(context, "JSON_EXTRACT(payload, '$.items[0].name')")
            ];
    }

    public static IEnumerable<object[]> NegativeMatrix()
    {
        var mutationTargets = new[]
        {
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.MsSqlServer
        };

        foreach (var target in mutationTargets)
        foreach (var context in ProjectionContexts)
            yield return
            [
                $"{target}__mutation-array-index__{context}",
                SqlAgentToolType.MySQL,
                target,
                Query(context, "JSON_SET(payload, '$.items[0].name', 'x')"),
                "json.path.mutation_array_index"
            ];

        var unsupportedExtractTargets = new[]
        {
            SqlAgentToolType.Oracle,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Firebird
        };

        foreach (var target in unsupportedExtractTargets)
        foreach (var context in ProjectionContexts)
            yield return
            [
                $"{target}__extract-unsupported__{context}",
                SqlAgentToolType.MySQL,
                target,
                Query(context, "JSON_EXTRACT(payload, '$.items[0].name')"),
                "JSON_EXTRACT"
            ];
    }

    [Fact]
    public void Matrices_HaveStableCoverage()
    {
        Assert.Equal(ExpectedPositiveCaseCount, PositiveMatrix().Count());
        Assert.Equal(ExpectedNegativeCaseCount, NegativeMatrix().Count());
    }

    [Theory]
    [MemberData(nameof(PositiveMatrix))]
    public void ArrayIndexExtraction_UsesProvenCrossProviderSubset(
        string name,
        SqlAgentToolType source,
        SqlAgentToolType target,
        string sql)
    {
        var command = Compile(sql, source, target);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.DoesNotContain("CORE_JSON_EXTRACT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(NegativeMatrix))]
    public void UnprovenJsonPathOrTargetSemantics_FailClosed(
        string name,
        SqlAgentToolType source,
        SqlAgentToolType target,
        string sql,
        string marker)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(sql, source, target));

        Assert.Contains(marker, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), name);
    }

    private static string Query(string context, string expression) =>
        context switch
        {
            "root" => $"SELECT {expression} AS value FROM events",
            "cte" => $"WITH x AS (SELECT {expression} AS value FROM events) SELECT value FROM x",
            "scalar-subquery" => $"SELECT (SELECT {expression} FROM events) AS value",
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source),
            target,
            new SqlPlanValidationContext("advanced-json-path-grammar-v1"),
            new SqlExecutionPlanPolicy());
}
