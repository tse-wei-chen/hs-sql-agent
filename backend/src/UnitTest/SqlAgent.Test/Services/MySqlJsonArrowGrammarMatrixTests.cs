using Xunit;

namespace SqlAgent.Test.Services;

public sealed class MySqlJsonArrowGrammarMatrixTests
{
    public const int ExpectedPositiveCaseCount = 9;
    public const int ExpectedNegativeCaseCount = 24;

    private static readonly string[] Contexts =
    [
        "root",
        "cte",
        "scalar-subquery"
    ];

    private static readonly SqlAgentToolType[] Targets =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Sqlite
    ];

    public static IEnumerable<object[]> PositiveMatrix()
    {
        foreach (var target in Targets)
        foreach (var context in Contexts)
            yield return
            [
                $"{target}__{context}",
                target,
                Query(context, "payload->'$.items[0].name'")
            ];
    }

    public static IEnumerable<object[]> WrongDialectMatrix()
    {
        foreach (var source in Enum.GetValues<SqlAgentToolType>().Where(
                     value => value != SqlAgentToolType.MySQL
                              && value != SqlAgentToolType.Postgres))
        foreach (var context in Contexts)
            yield return
            [
                $"{source}__{context}",
                source,
                Query(context, "payload->'$.id'")
            ];
    }

    public static IEnumerable<object[]> VersionProofMatrix()
    {
        foreach (var context in Contexts)
        {
            yield return
            [
                $"undeclared__{context}",
                null,
                Query(context, "payload->'$.id'")
            ];
            yield return
            [
                $"5.7.8__{context}",
                new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7, 8)),
                Query(context, "payload->'$.id'")
            ];
        }
    }

    public static IEnumerable<object[]> UnsupportedShapeMatrix()
    {
        foreach (var context in Contexts)
        {
            yield return
            [
                $"unquoted-arrow__{context}",
                Query(context, "payload->>'$.id'"),
                "json.operator.mysql_unquoted_arrow"
            ];
            yield return
            [
                $"expression-left__{context}",
                Query(context, "JSON_EXTRACT(payload, '$.root')->'$.id'"),
                "json.operator.mysql_arrow"
            ];
        }
    }

    [Fact]
    public void Matrices_HaveStableCoverage()
    {
        Assert.Equal(ExpectedPositiveCaseCount, PositiveMatrix().Count());
        Assert.Equal(12, WrongDialectMatrix().Count());
        Assert.Equal(6, VersionProofMatrix().Count());
        Assert.Equal(6, UnsupportedShapeMatrix().Count());
        Assert.Equal(
            ExpectedNegativeCaseCount,
            WrongDialectMatrix().Count() + VersionProofMatrix().Count() + UnsupportedShapeMatrix().Count());
    }

    [Theory]
    [MemberData(nameof(PositiveMatrix))]
    public void MySqlArrow_NormalizesToCanonicalJsonExtract(
        string name,
        SqlAgentToolType target,
        string sql)
    {
        var sourceProfile = MySqlProfile(8, 4, 0);
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL, sourceProfile);
        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            target,
            new SqlPlanValidationContext("mysql-json-arrow-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.DoesNotContain("->", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CORE_JSON_EXTRACT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(WrongDialectMatrix))]
    public void MySqlArrow_InWrongDialect_FailsAtDialectBoundary(
        string name,
        SqlAgentToolType source,
        string sql)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sql, source));

        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal("SQL_SOURCE_DIALECT_SYNTAX", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.DialectSyntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(VersionProofMatrix))]
    public void MySqlArrow_WithoutMinimumSourceVersion_FailsClosed(
        string name,
        SqlProviderCapabilityProfile? profile,
        string sql)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL, profile));

        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.Contains("5.7.9", error.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(UnsupportedShapeMatrix))]
    public void MySqlArrow_UnmodeledResultOrOperandShape_FailsClosed(
        string name,
        string sql,
        string capability)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL, MySqlProfile(8, 4, 0)));

        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.Contains(capability, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    private static string Query(string context, string expression) =>
        context switch
        {
            "root" => $"SELECT {expression} AS value FROM events",
            "cte" => $"WITH x AS (SELECT {expression} AS value FROM events) SELECT value FROM x",
            "scalar-subquery" => $"SELECT (SELECT {expression} FROM events) AS value",
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };

    private static SqlProviderCapabilityProfile MySqlProfile(int major, int minor, int build) =>
        new(SqlAgentToolType.MySQL, new Version(major, minor, build));
}
