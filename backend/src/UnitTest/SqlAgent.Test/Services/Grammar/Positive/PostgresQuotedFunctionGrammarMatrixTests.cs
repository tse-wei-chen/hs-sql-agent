using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresQuotedFunctionGrammarMatrixTests
{
    private sealed record IdentifierVariant(
        string Name,
        string Sql,
        string Rendered);

    private sealed record ContextVariant(
        string Name,
        Func<string, string> Build,
        string Tables);

    private sealed record NativeFunctionVariant(
        string Name,
        SqlAgentToolType Dialect,
        string FunctionSql,
        string Rendered);

    private static readonly IdentifierVariant[] Identifiers =
    [
        new("quoted-name", "\"lower\"", "\"lower\""),
        new("quoted-function", "pg_catalog.\"lower\"", "PG_CATALOG.\"lower\""),
        new("quoted-schema", "\"pg_catalog\".lower", "\"pg_catalog\".LOWER"),
        new("quoted-both", "\"pg_catalog\".\"lower\"", "\"pg_catalog\".\"lower\""),
        new("quoted-core-like", "\"CORE_DATE_ADD\"", "\"CORE_DATE_ADD\"")
    ];

    private static readonly ContextVariant[] Contexts =
    [
        new(
            "projection",
            fn => $"SELECT {fn}(name) AS normalized FROM users",
            "users"),
        new(
            "predicate",
            fn => $"SELECT id FROM users WHERE {fn}(name) = 'alice'",
            "users"),
        new(
            "cte-body",
            fn => $"WITH x AS (SELECT {fn}(name) AS normalized FROM users) SELECT normalized FROM x",
            "users"),
        new(
            "scalar-subquery",
            fn => $"SELECT (SELECT {fn}(name) FROM users LIMIT 1) AS normalized FROM outer_users",
            "outer_users,users")
    ];

    private static readonly NativeFunctionVariant[] NativeQuotedSources =
    [
        new("postgres-double-quote", SqlAgentToolType.Postgres, "\"NormalizeName\"", "\"NormalizeName\""),
        new("mysql-backtick", SqlAgentToolType.MySQL, "`NormalizeName`", "`NormalizeName`"),
        new("sqlserver-bracket-qualified", SqlAgentToolType.MsSqlServer, "[dbo].[NormalizeName]", "[dbo].[NormalizeName]"),
        new("sqlite-double-quote", SqlAgentToolType.Sqlite, "\"NormalizeName\"", "\"NormalizeName\""),
        new("oracle-double-quote", SqlAgentToolType.Oracle, "\"NormalizeName\"", "\"NormalizeName\""),
        new("firebird-double-quote", SqlAgentToolType.Firebird, "\"NormalizeName\"", "\"NormalizeName\"")
    ];

    private static readonly NativeFunctionVariant[] NativeQualifiedSources =
    [
        new("postgres-qualified", SqlAgentToolType.Postgres, "analytics.NormalizeName", "ANALYTICS.NORMALIZENAME"),
        new("mysql-qualified", SqlAgentToolType.MySQL, "analytics.NormalizeName", "analytics.NormalizeName"),
        new("sqlserver-qualified", SqlAgentToolType.MsSqlServer, "dbo.NormalizeName", "dbo.NormalizeName"),
        new("oracle-qualified", SqlAgentToolType.Oracle, "analytics.NormalizeName", "ANALYTICS.NORMALIZENAME"),
        new("firebird-qualified", SqlAgentToolType.Firebird, "analytics.NormalizeName", "ANALYTICS.NORMALIZENAME")
    ];

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var identifier in Identifiers)
        foreach (var context in Contexts)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(identifier.Name, context.Name),
                context.Build(identifier.Sql),
                identifier.Rendered,
                context.Tables
            ];
        }
    }

    public static IEnumerable<object[]> NativeQuotedSourceMatrix()
    {
        foreach (var source in NativeQuotedSources)
        {
            yield return
            [
                source.Name,
                source.Dialect,
                $"SELECT {source.FunctionSql}(name) AS normalized FROM users",
                source.Rendered
            ];
        }
    }

    public static IEnumerable<object[]> NativeQualificationMatrix()
    {
        foreach (var source in NativeQualifiedSources)
        {
            yield return
            [
                source.Name,
                source.Dialect,
                $"SELECT {source.FunctionSql}(name) AS normalized FROM users",
                source.Rendered
            ];
        }
    }

    [Fact]
    public void Matrix_HasStableCombinatorialCoverage()
    {
        var cases = Matrix().ToArray();

        Assert.Equal(20, cases.Length);
        Assert.Equal(
            20,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void NativeSourceMatrices_HaveStableSixProviderCoverage()
    {
        var quoted = NativeQuotedSourceMatrix().ToArray();
        var qualified = NativeQualificationMatrix().ToArray();

        Assert.Equal(6, quoted.Length);
        Assert.Equal(6, quoted.Select(item => Assert.IsType<SqlAgentToolType>(item[1])).Distinct().Count());
        Assert.Equal(5, qualified.Length);
        Assert.DoesNotContain(
            qualified,
            item => Assert.IsType<SqlAgentToolType>(item[1]) == SqlAgentToolType.Sqlite);
    }

    public static IEnumerable<object[]> OpaqueModifierNegativeMatrix()
    {
        yield return
        [
            "quoted-distinct",
            "SELECT \"SUM\"(DISTINCT amount) FROM orders",
            "DISTINCT"
        ];
        yield return
        [
            "quoted-aggregate-order",
            "SELECT \"STRING_AGG\"(name ORDER BY name) FROM users",
            "aggregate-local"
        ];
        yield return
        [
            "quoted-filter",
            "SELECT \"SUM\"(amount) FILTER (WHERE status = 'open') FROM orders",
            "FILTER"
        ];
        yield return
        [
            "quoted-over",
            "SELECT \"ROW_NUMBER\"() OVER (ORDER BY id) FROM users",
            "OVER"
        ];
    }

    public static IEnumerable<object[]> UnsupportedSourceMatrix()
    {
        yield return
        [
            "sqlite-qualified",
            SqlAgentToolType.Sqlite,
            "SELECT analytics.NormalizeName(name) FROM users"
        ];
    }

    [Fact]
    public void OpaqueModifierNegativeMatrix_HasStableCoverage()
    {
        var cases = OpaqueModifierNegativeMatrix().ToArray();

        Assert.Equal(4, cases.Length);
        Assert.Equal(
            4,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(OpaqueModifierNegativeMatrix))]
    public void QuotedFunctionModifiers_WithoutModeledAggregateSemantics_FailClosed(
        string name,
        string sql,
        string messageFragment)
    {
        var error = Assert.Throws<SqlCompilationException>(
            () => Compile(sql, SqlAgentToolType.Postgres));

        Assert.Contains(messageFragment, error.Message, StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_SEMANTIC_VALIDATION_FAILED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Semantic, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
    }

    [Fact]
    public void UnsupportedSourceMatrix_HasStableCoverage()
    {
        var cases = UnsupportedSourceMatrix().ToArray();

        Assert.Single(cases);
    }

    [Fact]
    public void Parser_PreservesQuotedFunctionIdentifierParts()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT \"analytics\".\"NormalizeName\"(name) FROM users",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var call = Assert.IsType<FunctionCallExpr>(Assert.Single(select.Select).Expression);

        Assert.Collection(
            call.Name.Parts,
            schema =>
            {
                Assert.Equal("analytics", schema.Value);
                Assert.True(schema.WasQuoted);
            },
            function =>
            {
                Assert.Equal("NormalizeName", function.Value);
                Assert.True(function.WasQuoted);
            });
    }

    [Theory]
    [MemberData(nameof(UnsupportedSourceMatrix))]
    public void UnsupportedSourceDialects_FailAtSourceCapabilityBoundary(
        string name,
        SqlAgentToolType dialect,
        string sql)
    {
        var error = Assert.Throws<SqlParseException>(
            () => CoreSqlTextParser.ParseQuery(sql, dialect));

        Assert.Contains("function.qualified", error.Message, StringComparison.OrdinalIgnoreCase);
        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(NativeQuotedSourceMatrix))]
    public void NativeQuotedSources_ParseCompileAndRenderOnTheirOwnProvider(
        string name,
        SqlAgentToolType dialect,
        string sql,
        string expectedRenderedFunction)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
        var facts = SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Contains(
            facts.ReferencedTables,
            actual => string.Equals(actual, "users", StringComparison.OrdinalIgnoreCase));

        var command = Compile(sql, dialect, dialect);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains(expectedRenderedFunction, command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NativeQualificationMatrix))]
    public void NativeQualifiedSources_ParseCompileAndRenderOnTheirOwnProvider(
        string name,
        SqlAgentToolType dialect,
        string sql,
        string expectedRenderedFunction)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
        var command = Compile(sql, dialect, dialect);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains(expectedRenderedFunction, command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Matrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string sql,
        string expectedRenderedFunction,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var facts = SqlCoreInspection.GetQueryFacts(parsed);
        var expectedTables = expectedTablesCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(expectedTables.Length, facts.ReferencedTables.Count);
        foreach (var table in expectedTables)
        {
            Assert.Contains(
                facts.ReferencedTables,
                actual => string.Equals(actual, table, StringComparison.OrdinalIgnoreCase));
        }

        var command = Compile(sql, SqlAgentToolType.Postgres);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains(expectedRenderedFunction, command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Matrix_CrossProviderTargetFailsAtCapabilityBoundary(
        string name,
        string sql,
        string _,
        string tables)
    {
        Assert.False(string.IsNullOrWhiteSpace(tables), name);

        var error = Assert.Throws<SqlCompilationException>(
            () => Compile(sql, SqlAgentToolType.Sqlite));

        Assert.Contains("provider-bound", error.Message, StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_TARGET_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.TargetCapability, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
    }

    [Fact]
    public void UnquotedQualifiedFunction_CrossProviderTargetFailsAtQualifiedCapabilityBoundary()
    {
        var error = Assert.Throws<SqlCompilationException>(
            () => Compile(
                "SELECT pg_catalog.lower(name) FROM users",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL));

        Assert.Contains("function.qualified", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider-bound", error.Message, StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_TARGET_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.TargetCapability, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType target) =>
        Compile(sql, SqlAgentToolType.Postgres, target);

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source),
            target,
            new SqlPlanValidationContext(
                "cross-dialect-native-function-grammar-matrix-v2"),
            new SqlExecutionPlanPolicy());
}
