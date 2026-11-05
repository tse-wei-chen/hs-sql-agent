using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlServerProfileSensitiveGrammarMatrixTests
{
    private sealed record AggregateContext(
        Func<string, string> Sql,
        string[] PhysicalTables);

    private sealed record OrderingVariant(
        string Sql,
        int ExpectedCount,
        bool ContainsCoalesce,
        bool ContainsAscending);

    private static readonly SqlProviderCapabilityProfile Profile =
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(14, 0),
            CompatibilityLevel: 110);

    private static readonly GrammarVariant<AggregateContext>[] Contexts =
    [
        new(
            "root",
            new AggregateContext(
                aggregate => $"SELECT {aggregate} AS value FROM users",
                ["users"])),
        new(
            "cte-body",
            new AggregateContext(
                aggregate =>
                    $"WITH x AS (SELECT {aggregate} AS value FROM users) SELECT value FROM x",
                ["users"])),
        new(
            "scalar-subquery",
            new AggregateContext(
                aggregate =>
                    $"SELECT (SELECT {aggregate} FROM users) AS value FROM outer_users",
                ["users", "outer_users"])),
        new(
            "grouped",
            new AggregateContext(
                aggregate =>
                    $"SELECT category, {aggregate} AS value FROM users GROUP BY category",
                ["users"]))
    ];

    private static readonly GrammarVariant<OrderingVariant>[] Orderings =
    [
        new(
            "single-desc",
            new OrderingVariant(
                "created_at DESC",
                1,
                false,
                false)),
        new(
            "multi-direction",
            new OrderingVariant(
                "created_at DESC, name ASC",
                2,
                false,
                true)),
        new(
            "nested-expression",
            new OrderingVariant(
                "COALESCE(sort_key, 'fallback') DESC",
                1,
                true,
                false))
    ];

    private static readonly GrammarVariant<string>[] Separators =
    [
        new("comma", ","),
        new("pipe", "|"),
        new("semicolon", ";")
    ];

    public static IEnumerable<object[]> SqlServerStringAggregateProfileMatrix()
    {
        foreach (var (context, ordering, separator) in
                 SyntaxGrammarMatrix.Product(
                     Contexts,
                     Orderings,
                     Separators))
        {
            var aggregate =
                "STRING_AGG(name, '" +
                separator.Value +
                "') WITHIN GROUP (ORDER BY " +
                ordering.Value.Sql +
                ")";

            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "sqlserver-profile",
                    "string-agg-ordering",
                    context.Name,
                    ordering.Name,
                    separator.Name),
                context.Name,
                context.Value.Sql(aggregate),
                ordering.Value.ExpectedCount,
                ordering.Value.ContainsCoalesce,
                ordering.Value.ContainsAscending,
                separator.Value,
                SyntaxGrammarMatrix.ExpectedTables(
                    context.Value.PhysicalTables)
            ];
        }
    }

    [Fact]
    public void SqlServerStringAggregateProfileMatrix_IsCartesianAndCollisionFree()
    {
        var cases = SqlServerStringAggregateProfileMatrix().ToArray();

        Assert.Equal(36, cases.Length);
        Assert.Equal(
            36,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            36,
            cases.Select(item => Assert.IsType<string>(item[2]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(SqlServerStringAggregateProfileMatrix))]
    public void SqlServerStringAggregateProfileMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string context,
        string sql,
        int expectedOrderCount,
        bool containsCoalesce,
        bool containsAscending,
        string separator,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MsSqlServer,
            Profile);

        var function = AggregateFunction(
            parsed.Statement,
            context);
        Assert.Equal(
            AggregateOrderSyntaxKind.WithinGroup,
            function.AggregateOrderSyntax);
        Assert.Equal(
            expectedOrderCount,
            function.AggregateOrderBy.Length);

        AssertTables(
            name,
            parsed,
            expectedTablesCsv);

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext(
                "sqlserver-profile-sensitive-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy(),
            Profile,
            Profile);

        Assert.Contains("STRING_AGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "WITHIN GROUP (ORDER BY",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            command.Parameters,
            parameter => Equals(
                parameter.Value,
                separator));

        if (containsCoalesce)
        {
            Assert.Contains("COALESCE", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                command.Parameters,
                parameter => Equals(
                    parameter.Value,
                    "fallback"));
        }

        if (containsAscending)
            Assert.Contains("ASC", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedProfileSensitiveParityCorpus_CarriesSqlServerCompatibilityProof()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-profile-sensitive-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var sqlServerCases = document.RootElement
            .EnumerateArray()
            .Where(item => string.Equals(
                item.GetProperty("dialect").GetString(),
                "MsSqlServer",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(36, sqlServerCases.Length);
        Assert.All(
            sqlServerCases,
            item => Assert.Equal(
                110,
                item.GetProperty(
                    "sourceCompatibilityLevel").GetInt32()));
        Assert.All(
            sqlServerCases,
            item => Assert.Equal(
                110,
                item.GetProperty(
                    "targetCompatibilityLevel").GetInt32()));
        Assert.All(
            sqlServerCases,
            item => Assert.Equal(
                "14.0",
                item.GetProperty("sourceVersion").GetString()));
    }

    private static FunctionCallExpr AggregateFunction(
        SqlStatement statement,
        string context)
    {
        var select = context switch
        {
            "cte-body" =>
                Assert.IsType<SelectStatement>(
                    Assert.Single(
                        Assert.IsType<SelectStatement>(
                            statement).Ctes).Query),
            "scalar-subquery" =>
                Assert.IsType<SelectStatement>(
                    Assert.IsType<SubqueryExpr>(
                        Assert.Single(
                            Assert.IsType<SelectStatement>(
                                statement).Select).Expression).Query),
            _ => Assert.IsType<SelectStatement>(statement)
        };

        var item = context == "grouped"
            ? select.Select[1]
            : Assert.Single(select.Select);
        return Assert.IsType<FunctionCallExpr>(
            item.Expression);
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
}
