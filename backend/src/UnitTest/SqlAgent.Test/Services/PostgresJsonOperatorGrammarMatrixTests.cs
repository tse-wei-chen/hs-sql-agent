using HsSqlAgent.SqlCore.Core.Ast;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresJsonOperatorGrammarMatrixTests
{
    public const int ExpectedPositiveCaseCount = 18;
    public const int ExpectedNegativeCaseCount = 48;

    private static readonly string[] Contexts =
    [
        "root",
        "predicate",
        "order"
    ];

    private static readonly string[] PositiveExpressions =
    [
        "payload->'user'",
        "payload->>'user'",
        "payload->'$.id'",
        "payload->0",
        "payload->>-1",
        "payload->'user'->'name'"
    ];

    public static IEnumerable<object[]> PositiveMatrix()
    {
        foreach (var expression in PositiveExpressions)
        foreach (var context in Contexts)
            yield return
            [
                $"{expression.Replace(" ", "-", StringComparison.Ordinal)}__{context}",
                Query(context, expression)
            ];
    }

    public static IEnumerable<object[]> CrossTargetNegativeMatrix()
    {
        var targets = Enum.GetValues<SqlAgentToolType>()
            .Where(value => value != SqlAgentToolType.Postgres);

        foreach (var target in targets)
        foreach (var expression in new[] { "payload->'user'", "payload->>'user'" })
        foreach (var context in Contexts)
            yield return
            [
                $"{target}__{expression}__{context}",
                target,
                Query(context, expression)
            ];
    }

    public static IEnumerable<object[]> WrongDialectMatrix()
    {
        var sources = new[]
        {
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Firebird
        };

        foreach (var source in sources)
        foreach (var context in Contexts)
            yield return
            [
                $"{source}__{context}",
                source,
                Query(context, "payload->'user'")
            ];
    }

    public static IEnumerable<object[]> OldVersionMatrix()
    {
        foreach (var expression in new[] { "payload->'user'", "payload->>'user'" })
        foreach (var context in Contexts)
            yield return
            [
                $"{expression}__{context}",
                Query(context, expression)
            ];
    }

    [Fact]
    public void Matrices_HaveStableCoverage()
    {
        Assert.Equal(ExpectedPositiveCaseCount, PositiveMatrix().Count());
        Assert.Equal(30, CrossTargetNegativeMatrix().Count());
        Assert.Equal(12, WrongDialectMatrix().Count());
        Assert.Equal(6, OldVersionMatrix().Count());
        Assert.Equal(
            ExpectedNegativeCaseCount,
            CrossTargetNegativeMatrix().Count()
            + WrongDialectMatrix().Count()
            + OldVersionMatrix().Count());
    }

    [Fact]
    public void Parse_PostgresJsonArrow_PreservesDedicatedCompatibilityNode()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT payload->>'user' AS name FROM events",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var json = Assert.IsType<PostgresJsonAccessExpr>(
            Assert.Single(select.Select).Expression);

        Assert.Equal(PostgresJsonSelectorKind.Property, json.SelectorKind);
        Assert.Equal(JsonExtractionResultKind.Text, json.ResultKind);
        Assert.Equal("user", json.PropertyKey);
        Assert.False(json.ArrayIndex.HasValue);
    }

    [Theory]
    [MemberData(nameof(PositiveMatrix))]
    public void PostgresJsonArrow_ParsesBindsValidatesAndRendersNatively(
        string name,
        string sql)
    {
        var command = Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains("->", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JSONB_EXTRACT_PATH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CORE_JSON_EXTRACT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(CrossTargetNegativeMatrix))]
    public void PostgresJsonArrow_CrossProviderLowering_FailsClosed(
        string name,
        SqlAgentToolType target,
        string sql)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, target));

        Assert.Contains("json.operator.postgres_arrow", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(error.Message), name);
    }

    [Theory]
    [MemberData(nameof(WrongDialectMatrix))]
    public void PostgresJsonArrow_InNonArrowDialect_FailsAtSourceBoundary(
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
    [MemberData(nameof(OldVersionMatrix))]
    public void PostgresJsonArrow_ExplicitPre93SourceProfile_FailsClosed(
        string name,
        string sql)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                sql,
                SqlAgentToolType.Postgres,
                new SqlProviderCapabilityProfile(
                    SqlAgentToolType.Postgres,
                    new Version(9, 2))));

        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.Contains("9.3", error.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    private static string Query(string context, string expression) =>
        context switch
        {
            "root" => $"SELECT {expression} AS value FROM events",
            "predicate" => $"SELECT id FROM events WHERE {expression} IS NOT NULL",
            "order" => $"SELECT id FROM events ORDER BY {expression}",
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            target,
            new SqlPlanValidationContext("postgres-json-operator-v1"),
            new SqlExecutionPlanPolicy());
}
