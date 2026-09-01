using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeSyntaxMutationMatrixTests
{
    private sealed record MutationPlacement(
        string BaselineSql,
        string MutatedSql);

    private sealed record MutationFamily(
        string Name,
        IReadOnlyList<GrammarVariant<SqlAgentToolType>> Dialects,
        IReadOnlyList<GrammarVariant<MutationPlacement>> Placements,
        Type ExceptionType,
        string DiagnosticCode,
        SqlDiagnosticStage DiagnosticStage,
        SqlDiagnosticCategory DiagnosticCategory,
        string MessageFragment,
        string SpanText);

    private static readonly GrammarVariant<SqlAgentToolType>[] NonPostgresDialects =
    [
        new("mysql", SqlAgentToolType.MySQL),
        new("sqlserver", SqlAgentToolType.MsSqlServer),
        new("sqlite", SqlAgentToolType.Sqlite),
        new("oracle", SqlAgentToolType.Oracle),
        new("firebird", SqlAgentToolType.Firebird)
    ];

    private static readonly GrammarVariant<SqlAgentToolType>[] NonLimitDialects =
    [
        new("sqlserver", SqlAgentToolType.MsSqlServer),
        new("oracle", SqlAgentToolType.Oracle),
        new("firebird", SqlAgentToolType.Firebird)
    ];

    private static readonly GrammarVariant<SqlAgentToolType>[] NoNullOrderingDialects =
    [
        new("mysql", SqlAgentToolType.MySQL),
        new("sqlserver", SqlAgentToolType.MsSqlServer)
    ];

    private static readonly GrammarVariant<SqlAgentToolType>[] NoFetchDialects =
    [
        new("mysql", SqlAgentToolType.MySQL),
        new("sqlite", SqlAgentToolType.Sqlite)
    ];

    private static readonly GrammarVariant<MutationPlacement>[] PostgresCastPlacements =
    [
        new(
            "projection",
            new(
                "SELECT CAST(id AS VARCHAR(20)) FROM users",
                "SELECT id::VARCHAR(20) FROM users")),
        new(
            "predicate",
            new(
                "SELECT id FROM users WHERE CAST(id AS VARCHAR(20)) = '1'",
                "SELECT id FROM users WHERE id::VARCHAR(20) = '1'")),
        new(
            "cte-body",
            new(
                "WITH x AS (SELECT CAST(id AS VARCHAR(20)) AS id FROM users) SELECT id FROM x",
                "WITH x AS (SELECT id::VARCHAR(20) AS id FROM users) SELECT id FROM x")),
        new(
            "nested-subquery",
            new(
                "SELECT id FROM users WHERE id IN (SELECT CAST(id AS VARCHAR(20)) FROM orders)",
                "SELECT id FROM users WHERE id IN (SELECT id::VARCHAR(20) FROM orders)"))
    ];

    private static readonly GrammarVariant<MutationPlacement>[] LimitPlacements =
    [
        new(
            "root-tail",
            new(
                "SELECT id FROM users",
                "SELECT id FROM users LIMIT 5")),
        new(
            "cte-body",
            new(
                "WITH x AS (SELECT id FROM users) SELECT id FROM x",
                "WITH x AS (SELECT id FROM users LIMIT 5) SELECT id FROM x")),
        new(
            "derived-source",
            new(
                "SELECT q.id FROM (SELECT id FROM users) q",
                "SELECT q.id FROM (SELECT id FROM users LIMIT 5) q")),
        new(
            "nested-subquery",
            new(
                "SELECT id FROM users WHERE id IN (SELECT user_id FROM orders)",
                "SELECT id FROM users WHERE id IN (SELECT user_id FROM orders LIMIT 5)"))
    ];

    private static readonly GrammarVariant<MutationPlacement>[] NullOrderingPlacements =
    [
        new(
            "root-order",
            new(
                "SELECT amount FROM orders ORDER BY amount",
                "SELECT amount FROM orders ORDER BY amount NULLS FIRST")),
        new(
            "window-order",
            new(
                "SELECT ROW_NUMBER() OVER (ORDER BY amount) FROM orders",
                "SELECT ROW_NUMBER() OVER (ORDER BY amount NULLS FIRST) FROM orders"))
    ];

    private static readonly GrammarVariant<MutationPlacement>[] FetchPlacements =
    [
        new(
            "root-tail",
            new(
                "SELECT id FROM users",
                "SELECT id FROM users FETCH FIRST 5 ROWS ONLY")),
        new(
            "cte-body",
            new(
                "WITH x AS (SELECT id FROM users) SELECT id FROM x",
                "WITH x AS (SELECT id FROM users FETCH FIRST 5 ROWS ONLY) SELECT id FROM x"))
    ];

    private static readonly MutationFamily[] Families =
    [
        new(
            "postgres-cast-spelling",
            NonPostgresDialects,
            PostgresCastPlacements,
            typeof(SqlParseException),
            "SQL_SOURCE_DIALECT_SYNTAX",
            SqlDiagnosticStage.SourceValidation,
            SqlDiagnosticCategory.DialectSyntax,
            "::",
            "::"),
        new(
            "limit-spelling",
            NonLimitDialects,
            LimitPlacements,
            typeof(SqlParseException),
            "SQL_PARSE_GRAMMAR",
            SqlDiagnosticStage.Parse,
            SqlDiagnosticCategory.Syntax,
            "LIMIT",
            "5"),
        new(
            "nulls-first",
            NoNullOrderingDialects,
            NullOrderingPlacements,
            typeof(SqlCompilationException),
            "SQL_SOURCE_CAPABILITY_REJECTED",
            SqlDiagnosticStage.SourceValidation,
            SqlDiagnosticCategory.Capability,
            "NULLS FIRST",
            "FIRST"),
        new(
            "fetch-spelling",
            NoFetchDialects,
            FetchPlacements,
            typeof(SqlParseException),
            "SQL_PARSE_GRAMMAR",
            SqlDiagnosticStage.Parse,
            SqlDiagnosticCategory.Syntax,
            "FETCH FIRST/NEXT",
            "FIRST")
    ];

    public static IEnumerable<object[]> NegativeSyntaxMutationMatrix()
    {
        foreach (var family in Families)
        foreach (var dialect in family.Dialects)
        foreach (var placement in family.Placements)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    family.Name,
                    dialect.Name,
                    placement.Name),
                dialect.Value,
                placement.Value.BaselineSql,
                placement.Value.MutatedSql,
                family.ExceptionType,
                family.DiagnosticCode,
                family.DiagnosticStage,
                family.DiagnosticCategory,
                family.MessageFragment,
                family.SpanText
            ];
        }
    }

    [Fact]
    public void NegativeSyntaxMutationMatrix_IsCombinatorialAndCollisionFree()
    {
        var cases = NegativeSyntaxMutationMatrix().ToArray();
        var expectedCount = Families.Sum(
            family => family.Dialects.Count * family.Placements.Count);

        Assert.Equal(40, expectedCount);
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
    [MemberData(nameof(NegativeSyntaxMutationMatrix))]
    public void NegativeSyntaxMutationMatrix_FailsClosedAtExactTypedBoundary(
        string name,
        SqlAgentToolType dialect,
        string baselineSql,
        string mutatedSql,
        Type expectedExceptionType,
        string expectedDiagnosticCode,
        SqlDiagnosticStage expectedDiagnosticStage,
        SqlDiagnosticCategory expectedDiagnosticCategory,
        string expectedMessageFragment,
        string expectedSpanText)
    {
        var baseline = CoreSqlTextParser.ParseQuery(
            baselineSql,
            dialect);

        Assert.NotNull(baseline.Statement);

        var error = Record.Exception(
            () => CoreSqlTextParser.ParseQuery(
                mutatedSql,
                dialect));

        Assert.NotNull(error);
        Assert.Equal(expectedExceptionType, error.GetType());
        Assert.Contains(
            expectedMessageFragment,
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);

        Assert.Equal(expectedDiagnosticCode, diagnostic.Code);
        Assert.Equal(expectedDiagnosticStage, diagnostic.Stage);
        Assert.Equal(expectedDiagnosticCategory, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length > 0, name);
        Assert.True(diagnostic.Span.End <= mutatedSql.Length, name);

        var actualSpanText = mutatedSql.Substring(
            diagnostic.Span.Start,
            diagnostic.Span.Length);

        Assert.Equal(
            expectedSpanText,
            actualSpanText,
            ignoreCase: true);
    }
}
