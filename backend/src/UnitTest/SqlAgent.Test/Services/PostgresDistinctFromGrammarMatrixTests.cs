using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresDistinctFromGrammarMatrixTests
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

    private static readonly SqlAgentToolType[] UnsupportedDialects =
    [
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Firebird
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
                SyntaxGrammarMatrix.CaseName(op.Name, operands.Name, context.Name),
                context.Value.Build(expression),
                op.Value.Sql,
                context.Value.Tables
            ];
        }
    }

    public static IEnumerable<object[]> UnsupportedSourceMatrix()
    {
        foreach (var dialect in UnsupportedDialects)
        foreach (var op in Operators)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(dialect.ToString(), op.Name, "source"),
                dialect,
                $"SELECT a {op.Value.Sql} b FROM comparisons"
            ];
        }
    }

    public static IEnumerable<object[]> UnsupportedTargetMatrix()
    {
        foreach (var target in UnsupportedDialects)
        foreach (var op in Operators)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(target.ToString(), op.Name, "where-target"),
                target,
                $"SELECT id FROM comparisons WHERE a {op.Value.Sql} b"
            ];
            yield return
            [
                SyntaxGrammarMatrix.CaseName(target.ToString(), op.Name, "cte-target"),
                target,
                $"WITH x AS (SELECT id FROM comparisons WHERE a {op.Value.Sql} b) SELECT id FROM x"
            ];
        }
    }

    [Fact]
    public void DistinctFromMatrices_HaveStableCoverage()
    {
        Assert.Equal(32, PositiveMatrix().Count());
        Assert.Equal(10, UnsupportedSourceMatrix().Count());
        Assert.Equal(20, UnsupportedTargetMatrix().Count());
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
    public void PositiveMatrix_ParsesBindsValidatesCompilesAndRenders(
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
    [MemberData(nameof(UnsupportedSourceMatrix))]
    public void UnsupportedSourceDialects_FailAtSourceCapabilityBoundary(
        string name,
        SqlAgentToolType sourceDialect,
        string sql)
    {
        var result = SqlCoreFacade.TryCompileQuery(
            sql,
            sourceDialect,
            sourceDialect,
            new SqlPlanValidationContext(
                "postgres-distinct-from-source-negative-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(result.Success, name);
        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.Contains(
            "operator.is_distinct_from",
            diagnostic.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(UnsupportedTargetMatrix))]
    public void UnsupportedTargets_FailAtTargetCapabilityBoundary(
        string name,
        SqlAgentToolType targetDialect,
        string sql)
    {
        var error = Assert.Throws<SqlCompilationException>(
            () => Compile(
                sql,
                SqlAgentToolType.Postgres,
                targetDialect));

        Assert.Contains(
            "operator.is_distinct_from",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_TARGET_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.TargetCapability, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                sql,
                sourceDialect),
            targetDialect,
            new SqlPlanValidationContext(
                "postgres-distinct-from-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy());
}
