using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeGrammarMutationMatrixTests
{
    private sealed record InvalidQueryVariant(
        SqlAgentToolType Dialect,
        Func<string, string> Query);

    private static readonly GrammarVariant<SqlAgentToolType>[] AllDialects =
        Enum.GetValues<SqlAgentToolType>()
            .Select(dialect => new GrammarVariant<SqlAgentToolType>(
                dialect.ToString(),
                dialect))
            .ToArray();

    private static readonly GrammarVariant<string>[] UniversalMalformedGrammar =
    [
        new(
            "cte-missing-as",
            "WITH x (SELECT id FROM users) SELECT id FROM x"),
        new(
            "cte-empty-column-list",
            "WITH x() AS (SELECT id FROM users) SELECT id FROM x"),
        new(
            "chained-comparison",
            "SELECT id FROM users WHERE id = 1 = 1"),
        new(
            "multiple-statements",
            "SELECT id FROM users; SELECT id FROM orders"),
        new(
            "cast-too-many-precision-components",
            "SELECT CAST(id AS DECIMAL(10,2,3)) FROM users")
    ];

    private static readonly GrammarVariant<SqlAgentToolType>[] NonPostgresDialects =
        AllDialects
            .Where(item => item.Value != SqlAgentToolType.Postgres)
            .ToArray();

    private static readonly GrammarVariant<Func<string, string>>[] PostfixCastContexts =
    [
        new(
            "root",
            expression => $"SELECT {expression} AS id FROM users"),
        new(
            "cte-body",
            expression => $"WITH x AS (SELECT {expression} AS id FROM users) SELECT id FROM x"),
        new(
            "scalar-subquery",
            expression => $"SELECT (SELECT {expression} FROM users) AS id FROM outer_users"),
        new(
            "set-branch",
            expression => $"SELECT id FROM users UNION ALL SELECT {expression} FROM archive_users"),
        new(
            "predicate",
            expression => $"SELECT id FROM users WHERE {expression} = 1"),
        new(
            "in-subquery",
            expression => $"SELECT id FROM users WHERE id IN (SELECT {expression} FROM archive_users)")
    ];

    private static readonly GrammarVariant<InvalidQueryVariant>[] WrongRowLimitForms =
    [
        new(
            "postgres-top",
            new InvalidQueryVariant(
                SqlAgentToolType.Postgres,
                table => $"SELECT TOP 1 id FROM {table}")),
        new(
            "mysql-fetch",
            new InvalidQueryVariant(
                SqlAgentToolType.MySQL,
                table => $"SELECT id FROM {table} FETCH FIRST 1 ROWS ONLY")),
        new(
            "sqlite-fetch",
            new InvalidQueryVariant(
                SqlAgentToolType.Sqlite,
                table => $"SELECT id FROM {table} FETCH FIRST 1 ROWS ONLY")),
        new(
            "sqlserver-limit",
            new InvalidQueryVariant(
                SqlAgentToolType.MsSqlServer,
                table => $"SELECT id FROM {table} LIMIT 1")),
        new(
            "oracle-limit",
            new InvalidQueryVariant(
                SqlAgentToolType.Oracle,
                table => $"SELECT id FROM {table} LIMIT 1")),
        new(
            "firebird-limit",
            new InvalidQueryVariant(
                SqlAgentToolType.Firebird,
                table => $"SELECT id FROM {table} LIMIT 1"))
    ];

    private static readonly GrammarVariant<Func<Func<string, string>, string>>[] QueryContexts =
    [
        new(
            "root",
            query => query("users")),
        new(
            "cte-body",
            query => $"WITH x AS ({query("users")}) SELECT id FROM x"),
        new(
            "scalar-subquery",
            query => $"SELECT ({query("users")}) AS id FROM outer_users"),
        new(
            "derived-source",
            query => $"SELECT q.id FROM ({query("users")}) q"),
        new(
            "in-subquery",
            query => $"SELECT id FROM outer_users WHERE id IN ({query("users")})")
    ];

    private static readonly GrammarVariant<SqlAgentToolType>[] NoNullOrderingDialects =
    [
        new("mysql", SqlAgentToolType.MySQL),
        new("sqlserver", SqlAgentToolType.MsSqlServer)
    ];

    private static readonly GrammarVariant<Func<string, string>>[] NullOrderingContexts =
    [
        new(
            "root-order",
            modifier => $"SELECT amount FROM orders ORDER BY amount {modifier}"),
        new(
            "window-order",
            modifier => $"SELECT ROW_NUMBER() OVER (ORDER BY amount {modifier}) FROM orders")
    ];

    public static IEnumerable<object[]> UniversalMalformedGrammarMatrix()
    {
        foreach (var (dialect, mutation) in
                 SyntaxGrammarMatrix.Product(AllDialects, UniversalMalformedGrammar))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    mutation.Name),
                dialect.Value,
                mutation.Value
            ];
        }
    }

    public static IEnumerable<object[]> WrongDialectPostfixCastMatrix()
    {
        foreach (var (dialect, context) in
                 SyntaxGrammarMatrix.Product(NonPostgresDialects, PostfixCastContexts))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    "postgres-postfix-cast",
                    context.Name),
                dialect.Value,
                context.Value("id::bigint")
            ];
        }
    }

    public static IEnumerable<object[]> WrongDialectRowLimitMatrix()
    {
        foreach (var (rowLimit, context) in
                 SyntaxGrammarMatrix.Product(WrongRowLimitForms, QueryContexts))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    rowLimit.Name,
                    context.Name),
                rowLimit.Value.Dialect,
                context.Value(rowLimit.Value.Query)
            ];
        }
    }

    public static IEnumerable<object[]> WrongDialectNullOrderingMatrix()
    {
        foreach (var (dialect, context) in
                 SyntaxGrammarMatrix.Product(NoNullOrderingDialects, NullOrderingContexts))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    "nulls-first",
                    context.Name),
                dialect.Value,
                context.Value("NULLS FIRST")
            ];
        }
    }

    [Fact]
    public void NegativeGrammarMatrices_HaveStableCoverage()
    {
        var malformed = UniversalMalformedGrammarMatrix().ToArray();
        var postfix = WrongDialectPostfixCastMatrix().ToArray();
        var rowLimit = WrongDialectRowLimitMatrix().ToArray();
        var nullOrdering = WrongDialectNullOrderingMatrix().ToArray();

        Assert.Equal(30, malformed.Length);
        Assert.Equal(30, postfix.Length);
        Assert.Equal(30, rowLimit.Length);
        Assert.Equal(4, nullOrdering.Length);
        Assert.Equal(
            94,
            malformed
                .Concat(postfix)
                .Concat(rowLimit)
                .Concat(nullOrdering)
                .Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(UniversalMalformedGrammarMatrix))]
    public void UniversalMalformedGrammar_FailsClosedAtTypedParseStage(
        string name,
        SqlAgentToolType dialect,
        string sql) =>
        AssertTypedParseGrammarFailure(name, dialect, sql);

    [Theory]
    [MemberData(nameof(WrongDialectRowLimitMatrix))]
    public void WrongDialectRowLimit_InNestedContexts_FailsClosedAtTypedParseStage(
        string name,
        SqlAgentToolType dialect,
        string sql) =>
        AssertTypedParseGrammarFailure(name, dialect, sql);

    [Theory]
    [MemberData(nameof(WrongDialectPostfixCastMatrix))]
    public void PostgresPostfixCast_InNestedNonPostgresContexts_FailsAtSourceDialectBoundary(
        string name,
        SqlAgentToolType dialect,
        string sql)
    {
        var result = TryCompile(dialect, sql);

        Assert.False(result.Success, name);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SOURCE_DIALECT_SYNTAX", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.DialectSyntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.Equal(2, diagnostic.Span.Length);
        Assert.Equal(
            "::",
            sql.Substring(diagnostic.Span.Start, diagnostic.Span.Length),
            ignoreCase: true);
    }

    [Theory]
    [MemberData(nameof(WrongDialectNullOrderingMatrix))]
    public void NullOrdering_InUnsupportedSourceDialects_FailsAtSourceCapabilityBoundary(
        string name,
        SqlAgentToolType dialect,
        string sql)
    {
        var result = TryCompile(dialect, sql);

        Assert.False(result.Success, name);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    private static void AssertTypedParseGrammarFailure(
        string name,
        SqlAgentToolType dialect,
        string sql)
    {
        var result = TryCompile(dialect, sql);

        Assert.False(result.Success, name);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_PARSE_GRAMMAR", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Parse, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    private static SqlCoreTryResult<CompiledSqlCommand> TryCompile(
        SqlAgentToolType dialect,
        string sql) =>
        SqlCoreFacade.TryCompileQuery(
            sql,
            dialect,
            dialect,
            new SqlPlanValidationContext(
                "negative-combinatorial-grammar-matrix-v2"),
            new SqlExecutionPlanPolicy());
}
