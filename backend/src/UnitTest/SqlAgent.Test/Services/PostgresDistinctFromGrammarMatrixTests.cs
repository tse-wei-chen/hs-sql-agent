using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CrossDialectDistinctFromGrammarMatrixTests
{
    private sealed record OperatorVariant(
        string Name,
        string Sql);

    private sealed record OperandVariant(
        string Name,
        string Left,
        string Right);

    private sealed record ContextVariant(
        string Name,
        Func<string, string> Build,
        string Tables);

    private sealed record TargetVariant(
        string Name,
        SqlAgentToolType Provider,
        SqlProviderCapabilityProfile? Profile,
        string ExpectedMarker);

    private sealed record SourceVariant(
        string Name,
        SqlAgentToolType Provider,
        SqlProviderCapabilityProfile? Profile,
        string DistinctSql,
        string NotDistinctSql,
        string ExpectedMarker);

    private static readonly GrammarVariant<OperatorVariant>[] Operators =
    [
        new("distinct", new("distinct", "IS DISTINCT FROM")),
        new("not-distinct", new("not-distinct", "IS NOT DISTINCT FROM"))
    ];

    private static readonly GrammarVariant<OperandVariant>[] Operands =
    [
        new("column-column", new("column-column", "a", "b")),
        new("column-null", new("column-null", "a", "NULL")),
        new("literal-null", new("literal-null", "1", "NULL")),
        new("coalesce-column", new("coalesce-column", "COALESCE(a, 0)", "b"))
    ];

    private static readonly GrammarVariant<ContextVariant>[] Contexts =
    [
        new(
            "projection",
            new(
                "projection",
                expression => $"SELECT {expression} AS different FROM comparisons",
                "comparisons")),
        new(
            "predicate",
            new(
                "predicate",
                expression => $"SELECT id FROM comparisons WHERE {expression}",
                "comparisons")),
        new(
            "cte-body",
            new(
                "cte-body",
                expression => $"WITH x AS (SELECT id, {expression} AS different FROM comparisons) SELECT id FROM x WHERE different = TRUE",
                "comparisons")),
        new(
            "scalar-subquery",
            new(
                "scalar-subquery",
                expression => $"SELECT (SELECT {expression} FROM comparisons LIMIT 1) AS different FROM outer_rows",
                "outer_rows,comparisons"))
    ];

    private static readonly ContextVariant[] CrossTargetContexts =
    [
        new(
            "predicate",
            expression => $"SELECT id FROM comparisons WHERE {expression}",
            "comparisons"),
        new(
            "cte-predicate",
            expression => $"WITH x AS (SELECT id FROM comparisons WHERE {expression}) SELECT id FROM x",
            "comparisons")
    ];

    private static readonly TargetVariant[] CrossTargets =
    [
        new("mysql", SqlAgentToolType.MySQL, null, "<=>"),
        new("sqlite", SqlAgentToolType.Sqlite, null, "DISTINCT FROM"),
        new("firebird", SqlAgentToolType.Firebird, null, "DISTINCT FROM"),
        new("oracle", SqlAgentToolType.Oracle, null, "CASE WHEN"),
        new("sqlserver16", SqlAgentToolType.MsSqlServer, SqlServerProfile(16), "DISTINCT FROM")
    ];

    private static readonly SourceVariant[] NativeSources =
    [
        new(
            "sqlite",
            SqlAgentToolType.Sqlite,
            null,
            "a IS DISTINCT FROM b",
            "a IS NOT DISTINCT FROM b",
            "DISTINCT FROM"),
        new(
            "firebird",
            SqlAgentToolType.Firebird,
            null,
            "a IS DISTINCT FROM b",
            "a IS NOT DISTINCT FROM b",
            "DISTINCT FROM"),
        new(
            "sqlserver16",
            SqlAgentToolType.MsSqlServer,
            SqlServerProfile(16),
            "a IS DISTINCT FROM b",
            "a IS NOT DISTINCT FROM b",
            "DISTINCT FROM"),
        new(
            "mysql",
            SqlAgentToolType.MySQL,
            null,
            "NOT (a <=> b)",
            "a <=> b",
            "<=>")
    ];

    public static IEnumerable<object[]> PositiveMatrix()
    {
        foreach (var op in Operators)
        foreach (var operands in Operands)
        foreach (var context in Contexts)
        {
            var expression = $"{operands.Value.Left} {op.Value.Sql} {operands.Value.Right}";
            yield return
            [
                SyntaxGrammarMatrix.CaseName("postgres", op.Name, operands.Name, context.Name),
                context.Value.Build(expression),
                op.Value.Sql,
                context.Value.Tables
            ];
        }
    }

    public static IEnumerable<object[]> NativeSourceMatrix()
    {
        foreach (var source in NativeSources)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(source.Name, "distinct", "native-source"),
                source.Provider,
                source.Profile,
                $"SELECT id FROM comparisons WHERE {source.DistinctSql}",
                source.ExpectedMarker
            ];
            yield return
            [
                SyntaxGrammarMatrix.CaseName(source.Name, "not-distinct", "native-source"),
                source.Provider,
                source.Profile,
                $"SELECT id FROM comparisons WHERE {source.NotDistinctSql}",
                source.ExpectedMarker
            ];
        }
    }

    public static IEnumerable<object[]> CrossProviderTargetMatrix()
    {
        foreach (var target in CrossTargets)
        foreach (var op in Operators)
        foreach (var context in CrossTargetContexts)
        {
            var expression = $"a {op.Value.Sql} b";
            yield return
            [
                SyntaxGrammarMatrix.CaseName(target.Name, op.Name, context.Name),
                target.Provider,
                target.Profile,
                context.Build(expression),
                target.ExpectedMarker
            ];
        }
    }

    public static IEnumerable<object[]> UnsupportedSourceMatrix()
    {
        foreach (var op in Operators)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName("mysql-postgres-spelling", op.Name),
                SqlAgentToolType.MySQL,
                null,
                $"SELECT id FROM comparisons WHERE a {op.Value.Sql} b"
            ];
            yield return
            [
                SyntaxGrammarMatrix.CaseName("oracle-postgres-spelling", op.Name),
                SqlAgentToolType.Oracle,
                null,
                $"SELECT id FROM comparisons WHERE a {op.Value.Sql} b"
            ];
            yield return
            [
                SyntaxGrammarMatrix.CaseName("sqlserver15", op.Name),
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(15),
                $"SELECT id FROM comparisons WHERE a {op.Value.Sql} b"
            ];
        }
    }

    public static IEnumerable<object[]> UnsupportedTargetMatrix()
    {
        foreach (var op in Operators)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName("sqlserver15", op.Name, "predicate"),
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(15),
                $"SELECT id FROM comparisons WHERE a {op.Value.Sql} b",
                "ServerVersion 16.0+"
            ];
            yield return
            [
                SyntaxGrammarMatrix.CaseName("sqlserver15", op.Name, "cte"),
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(15),
                $"WITH x AS (SELECT id FROM comparisons WHERE a {op.Value.Sql} b) SELECT id FROM x",
                "ServerVersion 16.0+"
            ];
            yield return
            [
                SyntaxGrammarMatrix.CaseName("oracle-nonrepeatable", op.Name),
                SqlAgentToolType.Oracle,
                null,
                $"SELECT id FROM comparisons WHERE COALESCE(a, 0) {op.Value.Sql} b",
                "repeatable scalar operands"
            ];
        }
    }

    [Fact]
    public void DistinctFromMatrices_HaveStableCoverage()
    {
        Assert.Equal(32, PositiveMatrix().Count());
        Assert.Equal(8, NativeSourceMatrix().Count());
        Assert.Equal(20, CrossProviderTargetMatrix().Count());
        Assert.Equal(6, UnsupportedSourceMatrix().Count());
        Assert.Equal(6, UnsupportedTargetMatrix().Count());
    }

    [Fact]
    public void Parser_ProjectsDedicatedNullSafeOperatorToCompatibilityAst()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT a IS NOT DISTINCT FROM b FROM comparisons",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var binary = Assert.IsType<BinaryExpr>(Assert.Single(select.Select).Expression);

        Assert.Equal("IS NOT DISTINCT FROM", binary.Operator);
    }

    [Theory]
    [MemberData(nameof(PositiveMatrix))]
    public void PostgresSourceMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string sql,
        string expectedOperator,
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

        var command = Compile(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains(expectedOperator, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(NativeSourceMatrix))]
    public void NativeSourceDialects_CanonicalizeNullSafeComparison(
        string name,
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile,
        string sql,
        string expectedMarker)
    {
        var command = Compile(
            sql,
            provider,
            provider,
            profile,
            profile);

        Assert.Contains(expectedMarker, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
    }

    [Theory]
    [MemberData(nameof(CrossProviderTargetMatrix))]
    public void CanonicalNullSafeComparison_LowersAcrossSupportedTargets(
        string name,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile,
        string sql,
        string expectedMarker)
    {
        var command = Compile(
            sql,
            SqlAgentToolType.Postgres,
            targetProvider,
            null,
            targetProfile);

        Assert.Contains(expectedMarker, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
    }

    [Theory]
    [MemberData(nameof(UnsupportedSourceMatrix))]
    public void WrongNativeSourceSpelling_FailsAtSourceCapabilityBoundary(
        string name,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile,
        string sql)
    {
        var error = Assert.Throws<SqlCompilationException>(
            () => CoreSqlTextParser.ParseQuery(
                sql,
                sourceDialect,
                sourceProfile));

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
    }

    [Theory]
    [MemberData(nameof(UnsupportedTargetMatrix))]
    public void UnprovenTargetLowering_FailsClosedAtTargetCapability(
        string name,
        SqlAgentToolType targetDialect,
        SqlProviderCapabilityProfile? targetProfile,
        string sql,
        string messageFragment)
    {
        var error = Assert.Throws<SqlCompilationException>(
            () => Compile(
                sql,
                SqlAgentToolType.Postgres,
                targetDialect,
                null,
                targetProfile));

        Assert.Contains(messageFragment, error.Message, StringComparison.OrdinalIgnoreCase);
        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_TARGET_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.TargetCapability, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, null, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, null, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, null, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Oracle, null, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Firebird, null, SqlCapabilityStatus.Supported)]
    public void CapabilityMatrix_ReflectsCrossDialectLowering(
        SqlAgentToolType provider,
        string? _,
        SqlCapabilityStatus expected)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "operator.is_distinct_from");

        Assert.Equal(expected, capability.Status);
    }

    [Fact]
    public void CapabilityMatrix_SqlServerIsVersionGated()
    {
        var oldCapability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(15)).Capabilities,
            item => item.Id == "operator.is_distinct_from");
        var supportedCapability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(16)).Capabilities,
            item => item.Id == "operator.is_distinct_from");

        Assert.Equal(SqlCapabilityStatus.Rejected, oldCapability.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, supportedCapability.Status);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect,
        SqlProviderCapabilityProfile? sourceProfile = null,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            sourceDialect,
            sourceProfile);

        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetDialect,
            new SqlPlanValidationContext(
                "cross-dialect-distinct-from-grammar-v2"),
            new SqlExecutionPlanPolicy(),
            targetProfile);
    }

    private static SqlProviderCapabilityProfile SqlServerProfile(int major) =>
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(major, 0));
}
