using HsSqlAgent.SqlCore;
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

    private static readonly IdentifierVariant[] Identifiers =
    [
        new("quoted-name", "\"lower\"", "\"lower\""),
        new("quoted-function", "pg_catalog.\"lower\"", "PG_CATALOG.\"lower\""),
        new("quoted-schema", "\"pg_catalog\".lower", "\"pg_catalog\".LOWER"),
        new("quoted-both", "\"pg_catalog\".\"lower\"", "\"pg_catalog\".\"lower\"")
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

    [Fact]
    public void Matrix_HasStableCombinatorialCoverage()
    {
        var cases = Matrix().ToArray();

        Assert.Equal(16, cases.Length);
        Assert.Equal(
            16,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
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
        string __)
    {
        var error = Assert.Throws<SqlCompilationException>(
            () => Compile(sql, SqlAgentToolType.Sqlite));

        Assert.Contains("function.qualified", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sqlite", error.Message, StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_TARGET_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.TargetCapability, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                sql,
                SqlAgentToolType.Postgres),
            target,
            new SqlPlanValidationContext(
                "postgres-quoted-function-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy());
}
