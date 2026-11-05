using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class NegativeQuerySyntaxBoundaryMatrixTests
{
    private sealed record NegativeCase(
        SqlAgentToolType Dialect,
        string Name,
        string Sql,
        string ExceptionKind,
        string DiagnosticCode,
        SqlDiagnosticStage Stage,
        SqlDiagnosticCategory Category);

    private static readonly NegativeCase[] WrongDialectCases =
    [
        new(
            SqlAgentToolType.Postgres,
            "postgres-top",
            "SELECT TOP 1 id FROM users",
            nameof(SqlParseException),
            "SQL_PARSE_GRAMMAR",
            SqlDiagnosticStage.Parse,
            SqlDiagnosticCategory.Syntax),
        new(
            SqlAgentToolType.MySQL,
            "mysql-nulls-first",
            "SELECT amount FROM orders ORDER BY amount NULLS FIRST",
            nameof(SqlCompilationException),
            "SQL_SOURCE_CAPABILITY_REJECTED",
            SqlDiagnosticStage.SourceValidation,
            SqlDiagnosticCategory.Capability),
        new(
            SqlAgentToolType.MsSqlServer,
            "sqlserver-limit",
            "SELECT id FROM users LIMIT 5",
            nameof(SqlParseException),
            "SQL_PARSE_GRAMMAR",
            SqlDiagnosticStage.Parse,
            SqlDiagnosticCategory.Syntax),
        new(
            SqlAgentToolType.Sqlite,
            "sqlite-typed-date",
            "SELECT DATE '2026-08-24'",
            nameof(SqlParseException),
            "SQL_SOURCE_DIALECT_SYNTAX",
            SqlDiagnosticStage.SourceValidation,
            SqlDiagnosticCategory.DialectSyntax),
        new(
            SqlAgentToolType.Oracle,
            "oracle-limit",
            "SELECT id FROM users LIMIT 5",
            nameof(SqlParseException),
            "SQL_PARSE_GRAMMAR",
            SqlDiagnosticStage.Parse,
            SqlDiagnosticCategory.Syntax),
        new(
            SqlAgentToolType.Firebird,
            "firebird-limit",
            "SELECT id FROM users LIMIT 5",
            nameof(SqlParseException),
            "SQL_PARSE_GRAMMAR",
            SqlDiagnosticStage.Parse,
            SqlDiagnosticCategory.Syntax)
    ];

    public static IEnumerable<object[]> UniversalMalformedBoundaryMatrix()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            yield return
            [
                dialect,
                "cte-missing-as",
                "WITH x (SELECT id FROM users) SELECT id FROM x",
                nameof(SqlParseException),
                "SQL_PARSE_GRAMMAR",
                SqlDiagnosticStage.Parse,
                SqlDiagnosticCategory.Syntax
            ];
        }
    }

    public static IEnumerable<object[]> WrongDialectBoundaryMatrix() =>
        WrongDialectCases.Select(item => new object[]
        {
            item.Dialect,
            item.Name,
            item.Sql,
            item.ExceptionKind,
            item.DiagnosticCode,
            item.Stage,
            item.Category
        });

    [Fact]
    public void NegativeQuerySyntaxBoundaryMatrix_HasStableCoverage()
    {
        Assert.Equal(6, UniversalMalformedBoundaryMatrix().Count());
        Assert.Equal(6, WrongDialectBoundaryMatrix().Count());
    }

    [Theory]
    [MemberData(nameof(UniversalMalformedBoundaryMatrix))]
    [MemberData(nameof(WrongDialectBoundaryMatrix))]
    public void TypedQueryRuntime_PreservesTypedDiagnosticAcrossServerBoundary(
        SqlAgentToolType dialect,
        string name,
        string sql,
        string exceptionKind,
        string diagnosticCode,
        SqlDiagnosticStage expectedStage,
        SqlDiagnosticCategory expectedCategory)
    {
        var runtime = new TypedQueryRuntime();
        var provider = SyntaxBoundaryTestSupport.Provider(dialect);

        var error = Record.Exception(() =>
            runtime.Compile(
                provider.Object,
                sql,
                dialect,
                SyntaxBoundaryTestSupport.Policy(),
                allowedTables: null));

        Assert.NotNull(error);
        Assert.Equal(exceptionKind, error.GetType().Name);

        var diagnostic = error switch
        {
            SqlParseException parse => parse.Diagnostic,
            SqlCompilationException compilation => compilation.Diagnostic,
            _ => error.Data["HsSqlAgent.SqlCore.Diagnostic"] as SqlDiagnostic
        };

        Assert.NotNull(diagnostic);
        Assert.Equal(diagnosticCode, diagnostic.Code);
        Assert.Equal(expectedStage, diagnostic.Stage);
        Assert.Equal(expectedCategory, diagnostic.Category);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message), name);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length >= 0, name);
    }
}
