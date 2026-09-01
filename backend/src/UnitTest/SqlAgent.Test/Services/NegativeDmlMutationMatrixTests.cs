using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeDmlMutationMatrixTests
{
    private sealed record DmlMutationPlacement(
        string Name,
        string BaselineSql,
        string MutatedSql);

    private sealed record DmlSyntaxFamily(
        string Name,
        IReadOnlyList<GrammarVariant<SqlAgentToolType>> Dialects,
        IReadOnlyList<GrammarVariant<DmlMutationPlacement>> Placements,
        Type ExceptionType,
        string DiagnosticCode,
        SqlDiagnosticStage DiagnosticStage,
        SqlDiagnosticCategory DiagnosticCategory,
        string MessageFragment,
        string SpanText);

    private sealed record DmlCapabilityShape(
        string Name,
        string Sql);

    private sealed record DmlCapabilityFamily(
        string Name,
        SqlAgentToolType SourceDialect,
        SqlAgentToolType TargetDialect,
        IReadOnlyList<GrammarVariant<DmlCapabilityShape>> Shapes,
        string DiagnosticCode,
        SqlDiagnosticStage DiagnosticStage,
        SqlDiagnosticCategory DiagnosticCategory,
        string[] MessageFragments);

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

    private static readonly GrammarVariant<DmlMutationPlacement>[] PostgresCastPlacements =
    [
        new(
            "update-set",
            new(
                "update-set",
                "UPDATE users SET name = CAST(id AS VARCHAR(20)) WHERE id = 1",
                "UPDATE users SET name = id::VARCHAR(20) WHERE id = 1")),
        new(
            "delete-predicate",
            new(
                "delete-predicate",
                "DELETE FROM users WHERE CAST(id AS VARCHAR(20)) = '1'",
                "DELETE FROM users WHERE id::VARCHAR(20) = '1'")),
        new(
            "insert-value",
            new(
                "insert-value",
                "INSERT INTO users (name) VALUES (CAST('1' AS VARCHAR(20)))",
                "INSERT INTO users (name) VALUES ('1'::VARCHAR(20))"))
    ];

    private static readonly GrammarVariant<DmlMutationPlacement>[] LimitInsertSelectPlacements =
    [
        new(
            "insert-select",
            new(
                "insert-select",
                "INSERT INTO archive (id) SELECT id FROM users",
                "INSERT INTO archive (id) SELECT id FROM users LIMIT 5"))
    ];

    private static readonly DmlSyntaxFamily[] SyntaxFamilies =
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
            "limit-insert-select",
            NonLimitDialects,
            LimitInsertSelectPlacements,
            typeof(SqlParseException),
            "SQL_PARSE_GRAMMAR",
            SqlDiagnosticStage.Parse,
            SqlDiagnosticCategory.Syntax,
            "LIMIT",
            "5")
    ];

    private static readonly GrammarVariant<DmlCapabilityShape>[] ImplicitInsertShapes =
    [
        new(
            "values",
            new(
                "values",
                "INSERT INTO users VALUES (1, 'Alice')")),
        new(
            "insert-select",
            new(
                "insert-select",
                "INSERT INTO archive SELECT id, name FROM staged_users"))
    ];

    private static readonly GrammarVariant<DmlCapabilityShape>[] SqlServerUpdateFromShapes =
    [
        new(
            "simple",
            new(
                "simple",
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id")),
        new(
            "extra-predicate",
            new(
                "extra-predicate",
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id AND users.active = 1"))
    ];

    private static readonly GrammarVariant<DmlCapabilityShape>[] PostgresUpdateFromShapes =
    [
        new(
            "simple",
            new(
                "simple",
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id")),
        new(
            "extra-predicate",
            new(
                "extra-predicate",
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id AND users.active = TRUE"))
    ];

    private static readonly DmlCapabilityFamily[] CapabilityFamilies =
    [
        new(
            "postgres-implicit-insert-to-mysql",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            ImplicitInsertShapes,
            "SQL_SEMANTIC_VALIDATION_FAILED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Semantic,
            ["dml.insert_implicit_columns", "native-only"]),
        new(
            "sqlserver-update-from-to-postgres",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres,
            SqlServerUpdateFromShapes,
            "SQL_SEMANTIC_VALIDATION_FAILED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Semantic,
            ["dml.update.from", "native-only", "MsSqlServer", "Postgres"]),
        new(
            "postgres-update-from-to-sqlserver",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            PostgresUpdateFromShapes,
            "SQL_SEMANTIC_VALIDATION_FAILED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Semantic,
            ["dml.update.from", "native-only", "Postgres", "MsSqlServer"])
    ];

    public static IEnumerable<object[]> NegativeDmlSyntaxMutationMatrix()
    {
        foreach (var family in SyntaxFamilies)
        foreach (var dialect in family.Dialects)
        foreach (var placement in family.Placements)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "dml",
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

    public static IEnumerable<object[]> NegativeDmlCapabilityMatrix()
    {
        foreach (var family in CapabilityFamilies)
        foreach (var shape in family.Shapes)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "dml",
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
    public void NegativeDmlSyntaxMutationMatrix_IsCombinatorialAndCollisionFree()
    {
        var cases = NegativeDmlSyntaxMutationMatrix().ToArray();
        var expectedCount = SyntaxFamilies.Sum(
            family => family.Dialects.Count * family.Placements.Count);

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

    [Fact]
    public void NegativeDmlCapabilityMatrix_IsCombinatorialAndCollisionFree()
    {
        var cases = NegativeDmlCapabilityMatrix().ToArray();
        var expectedCount = CapabilityFamilies.Sum(
            family => family.Shapes.Count);

        Assert.Equal(6, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(NegativeDmlSyntaxMutationMatrix))]
    public void NegativeDmlSyntaxMutationMatrix_BaselineParsesButMutationFailsAtExactTypedBoundary(
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
        var baseline = CoreSqlTextParser.ParseDml(
            baselineSql,
            dialect);

        Assert.NotNull(baseline.Statement);

        var error = Record.Exception(
            () => CoreSqlTextParser.ParseDml(
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

    [Theory]
    [MemberData(nameof(NegativeDmlCapabilityMatrix))]
    public void NegativeDmlCapabilityMatrix_NativeSucceedsButCrossProviderFailsClosed(
        string name,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect,
        string sql,
        string expectedDiagnosticCode,
        SqlDiagnosticStage expectedDiagnosticStage,
        SqlDiagnosticCategory expectedDiagnosticCategory,
        string[] expectedMessageFragments)
    {
        var native = CompileDml(
            sql,
            sourceDialect,
            sourceDialect);

        Assert.False(string.IsNullOrWhiteSpace(native.Sql), name);

        var error = Record.Exception(
            () => CompileDml(
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

    private static CompiledSqlCommand CompileDml(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                sql,
                sourceDialect),
            targetDialect,
            new SqlPlanValidationContext(
                "negative-dml-capability-matrix-v1"),
            new DmlCompilationPolicy());
}
