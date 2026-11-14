using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class MySqlSessionModeGrammarMatrixTests
{
    private sealed record QueryContext(
        Func<string, string> Sql,
        string[] PhysicalTables);

    private sealed record IdentifierShape(
        string Sql,
        string Value,
        string RenderedMarker);

    private static readonly GrammarVariant<string>[] ConcatModes =
    [
        new("pipes-as-concat", "PIPES_AS_CONCAT"),
        new("ansi", "ANSI")
    ];

    private static readonly GrammarVariant<string>[] QuoteModes =
    [
        new("ansi-quotes", "ANSI_QUOTES"),
        new("ansi", "ANSI")
    ];

    private static readonly GrammarVariant<string>[] ConcatExpressions =
    [
        new("two-column", "first_name || last_name"),
        new("nested", "first_name || ' ' || last_name"),
        new("function-operand", "COALESCE(first_name, '') || last_name")
    ];

    private static readonly GrammarVariant<QueryContext>[] ConcatContexts =
    [
        new(
            "root",
            new QueryContext(
                expression => $"SELECT {expression} AS value FROM users",
                ["users"])),
        new(
            "cte-body",
            new QueryContext(
                expression =>
                    $"WITH x AS (SELECT {expression} AS value FROM users) SELECT value FROM x",
                ["users"])),
        new(
            "scalar-subquery",
            new QueryContext(
                expression =>
                    $"SELECT (SELECT {expression} FROM users) AS value FROM outer_users",
                ["users", "outer_users"])),
        new(
            "predicate",
            new QueryContext(
                expression =>
                    $"SELECT id FROM users WHERE {expression} = 'Ada Lovelace'",
                ["users"]))
    ];

    private static readonly GrammarVariant<IdentifierShape>[] IdentifierShapes =
    [
        new(
            "simple",
            new IdentifierShape(
                "\"display_name\"",
                "display_name",
                "\u0060display_name\u0060")),
        new(
            "escaped-quote",
            new IdentifierShape(
                "\"display\"\"name\"",
                "display\"name",
                "\u0060display\"name\u0060")),
        new(
            "backtick-content",
            new IdentifierShape(
                "\"display\u0060name\"",
                "display\u0060name",
                "\u0060display\u0060\u0060name\u0060"))
    ];

    private static readonly GrammarVariant<QueryContext>[] QuoteContexts =
    [
        new(
            "root",
            new QueryContext(
                identifier => $"SELECT {identifier} AS value FROM users",
                ["users"])),
        new(
            "cte-body",
            new QueryContext(
                identifier =>
                    $"WITH x AS (SELECT {identifier} AS value FROM users) SELECT value FROM x",
                ["users"])),
        new(
            "predicate",
            new QueryContext(
                identifier =>
                    $"SELECT {identifier} AS value FROM users WHERE {identifier} IS NOT NULL",
                ["users"])),
        new(
            "order",
            new QueryContext(
                identifier =>
                    $"SELECT {identifier} AS value FROM users ORDER BY {identifier}",
                ["users"]))
    ];

    public static IEnumerable<object[]> ConcatSessionModeMatrix()
    {
        foreach (var (mode, expression, context) in
                 SyntaxGrammarMatrix.Product(
                     ConcatModes,
                     ConcatExpressions,
                     ConcatContexts))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "mysql-session",
                    "concat",
                    mode.Name,
                    expression.Name,
                    context.Name),
                mode.Value,
                context.Value.Sql(expression.Value),
                SyntaxGrammarMatrix.ExpectedTables(
                    context.Value.PhysicalTables)
            ];
        }
    }

    public static IEnumerable<object[]> AnsiQuotesSessionModeMatrix()
    {
        foreach (var (mode, identifier, context) in
                 SyntaxGrammarMatrix.Product(
                     QuoteModes,
                     IdentifierShapes,
                     QuoteContexts))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "mysql-session",
                    "ansi-quotes",
                    mode.Name,
                    identifier.Name,
                    context.Name),
                mode.Value,
                context.Name,
                context.Value.Sql(identifier.Value.Sql),
                identifier.Value.Value,
                identifier.Value.RenderedMarker,
                SyntaxGrammarMatrix.ExpectedTables(
                    context.Value.PhysicalTables)
            ];
        }
    }

    [Fact]
    public void MySqlSessionModeMatrices_AreCartesianAndCollisionFree()
    {
        var concat = ConcatSessionModeMatrix().ToArray();
        var quotes = AnsiQuotesSessionModeMatrix().ToArray();
        var all = concat.Concat(quotes).ToArray();

        Assert.Equal(24, concat.Length);
        Assert.Equal(24, quotes.Length);
        Assert.Equal(48, all.Length);
        Assert.Equal(
            48,
            all.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(ConcatSessionModeMatrix))]
    public void ConcatSessionModeMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string mode,
        string sql,
        string expectedTablesCsv)
    {
        var profile = MySqlProfile(mode);
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MySQL,
            profile);

        AssertTables(name, parsed, expectedTablesCsv);

        var command = Compile(sql, profile);

        Assert.Contains("CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AnsiQuotesSessionModeMatrix))]
    public void AnsiQuotesSessionModeMatrix_PreservesQuoteIntentAndRendersSafely(
        string name,
        string mode,
        string context,
        string sql,
        string expectedValue,
        string renderedMarker,
        string expectedTablesCsv)
    {
        var profile = MySqlProfile(mode);
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MySQL,
            profile);

        AssertTables(name, parsed, expectedTablesCsv);

        var quotedSelect = NativeSelect(parsed.Statement, context);
        var column = Assert.IsType<ColumnExpr>(
            Assert.Single(quotedSelect.Select).Expression);
        var part = Assert.Single(column.Name.Parts);
        Assert.Equal(expectedValue, part.Value);
        Assert.True(part.WasQuoted, name);

        var command = Compile(sql, profile);

        Assert.Contains(renderedMarker, command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedProfileSensitiveParityCorpus_CoversMySqlSessionModes()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-profile-sensitive-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement
            .EnumerateArray()
            .Where(item => string.Equals(
                item.GetProperty("dialect").GetString(),
                "MySQL",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(48, cases.Length);
        Assert.Equal(
            48,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(
            cases,
            item => Assert.Equal(
                "8.4",
                item.GetProperty("sourceVersion").GetString()));
        Assert.All(
            cases,
            item => Assert.Single(
                item.GetProperty("sourceSessionModes").EnumerateArray()));
    }

    private static void AssertTables(
        string name,
        ParsedStatement parsed,
        string expectedTablesCsv)
    {
        var expectedTables = expectedTablesCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var facts = SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Equal(expectedTables.Length, facts.ReferencedTables.Count);
        foreach (var table in expectedTables)
        {
            Assert.Contains(
                facts.ReferencedTables,
                actual => string.Equals(
                    actual,
                    table,
                    StringComparison.OrdinalIgnoreCase));
        }

        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    private static SelectStatement NativeSelect(
        SqlStatement statement,
        string context)
    {
        var root = Assert.IsType<SelectStatement>(statement);
        if (context != "cte-body")
            return root;

        var cte = Assert.Single(root.Ctes);
        return Assert.IsType<SelectStatement>(cte.Query);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlProviderCapabilityProfile profile) =>
        SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext(
                "mysql-session-mode-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy(),
            profile,
            profile);

    private static SqlProviderCapabilityProfile MySqlProfile(string mode) =>
        new(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(
                [mode],
                StringComparer.OrdinalIgnoreCase));
}
