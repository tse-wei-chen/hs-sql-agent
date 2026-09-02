using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class AdvancedRegexGrammarMatrixTests
{
    public const int ExpectedPositiveCaseCount = 18;
    public const int ExpectedNegativeCaseCount = 30;

    private sealed record RegexSpelling(
        string Name,
        SqlAgentToolType Dialect,
        string Expression,
        string RenderMarker);

    private static readonly RegexSpelling[] PositiveSpellings =
    [
        new("postgres-match", SqlAgentToolType.Postgres, "name ~ '^a'", "~"),
        new("postgres-not-match", SqlAgentToolType.Postgres, "name !~ '^a'", "NOT"),
        new("mysql-regexp", SqlAgentToolType.MySQL, "name REGEXP '^a'", "REGEXP_LIKE("),
        new("mysql-rlike", SqlAgentToolType.MySQL, "name RLIKE '^a'", "REGEXP_LIKE("),
        new("mysql-not-regexp", SqlAgentToolType.MySQL, "name NOT REGEXP '^a'", "NOT"),
        new("mysql-not-rlike", SqlAgentToolType.MySQL, "name NOT RLIKE '^a'", "NOT")
    ];

    private static readonly string[] Contexts =
    [
        "select",
        "predicate",
        "order"
    ];

    public static IEnumerable<object[]> PositiveMatrix()
    {
        foreach (var spelling in PositiveSpellings)
        foreach (var context in Contexts)
            yield return
            [
                $"{spelling.Name}__{context}",
                spelling.Dialect,
                Query(context, spelling.Expression),
                spelling.RenderMarker
            ];
    }

    public static IEnumerable<object[]> WrongDialectMatrix()
    {
        var dialects = Enum.GetValues<SqlAgentToolType>();

        foreach (var dialect in dialects.Where(value => value != SqlAgentToolType.Postgres))
        foreach (var expression in new[] { "name ~ '^a'", "name !~ '^a'" })
            yield return
            [
                $"{dialect}__postgres-regex__{expression.Replace(" ", "-", StringComparison.Ordinal)}",
                dialect,
                Query("predicate", expression)
            ];

        foreach (var dialect in dialects.Where(value => value != SqlAgentToolType.MySQL))
        foreach (var expression in new[]
                 {
                     "name REGEXP '^a'",
                     "name RLIKE '^a'",
                     "name NOT REGEXP '^a'",
                     "name NOT RLIKE '^a'"
                 })
            yield return
            [
                $"{dialect}__mysql-regex__{expression.Replace(" ", "-", StringComparison.Ordinal)}",
                dialect,
                Query("predicate", expression)
            ];
    }

    [Fact]
    public void Matrices_HaveStableCoverage()
    {
        Assert.Equal(ExpectedPositiveCaseCount, PositiveMatrix().Count());
        Assert.Equal(ExpectedNegativeCaseCount, WrongDialectMatrix().Count());
    }

    [Theory]
    [MemberData(nameof(PositiveMatrix))]
    public void NativeRegexSpellings_ParseBindValidateCompileAndRender(
        string name,
        SqlAgentToolType sourceDialect,
        string sql,
        string renderMarker)
    {
        var command = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            sourceDialect,
            new SqlPlanValidationContext("advanced-regex-grammar-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains(renderMarker, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(WrongDialectMatrix))]
    public void NativeRegexSpellings_InWrongDialect_FailAtSourceDialectBoundary(
        string name,
        SqlAgentToolType sourceDialect,
        string sql)
    {
        var result = SqlCoreFacade.TryCompileQuery(
            sql,
            sourceDialect,
            sourceDialect,
            new SqlPlanValidationContext("advanced-regex-negative-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(result.Success, name);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SOURCE_DIALECT_SYNTAX", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.DialectSyntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    private static string Query(string context, string expression) =>
        context switch
        {
            "select" => $"SELECT {expression} AS matched FROM users",
            "predicate" => $"SELECT id FROM users WHERE {expression}",
            "order" => $"SELECT id FROM users ORDER BY {expression}",
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };
}
