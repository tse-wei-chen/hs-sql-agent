using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresGrammarMatrixTests
{
    private sealed record BodyVariant(
        string Sql,
        string RenderedFragment,
        string[] PhysicalTables);

    private sealed record RootVariant(
        string Sql,
        string RenderedFragment,
        string[] PhysicalTables);

    private sealed record TailVariant(
        string Sql,
        string? RenderedFragment);

    private static readonly GrammarVariant<Func<string, string>>[] CteForms =
    [
        new(
            "single",
            body => $"WITH recent AS ({body})"),
        new(
            "column-alias",
            body => $"WITH recent(id) AS ({body})"),
        new(
            "multiple-chained",
            body => $"WITH base AS ({body}), recent(id) AS (SELECT id FROM base)")
    ];

    private static readonly GrammarVariant<BodyVariant>[] Bodies =
    [
        new(
            "simple",
            new BodyVariant(
                "SELECT id FROM orders",
                "FROM orders",
                ["orders"])),
        new(
            "where",
            new BodyVariant(
                "SELECT id FROM orders WHERE active = TRUE",
                "WHERE active",
                ["orders"])),
        new(
            "join",
            new BodyVariant(
                "SELECT o.id AS id FROM orders o JOIN customers c ON o.customer_id = c.id",
                "JOIN customers",
                ["orders", "customers"])),
        new(
            "group-having",
            new BodyVariant(
                "SELECT customer_id AS id FROM orders GROUP BY customer_id HAVING SUM(amount) > 0",
                "HAVING",
                ["orders"])),
        new(
            "filter-window",
            new BodyVariant(
                "SELECT SUM(amount) FILTER (WHERE status = 'open') OVER (PARTITION BY customer_id ORDER BY created_at ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS id FROM orders",
                "FILTER (WHERE",
                ["orders"])),
        new(
            "subquery",
            new BodyVariant(
                "SELECT id FROM orders WHERE EXISTS (SELECT id FROM audit_events WHERE audit_events.order_id = orders.id)",
                "EXISTS",
                ["orders", "audit_events"])),
        new(
            "set-operation",
            new BodyVariant(
                "SELECT id FROM orders UNION ALL SELECT id FROM archive_orders",
                "UNION ALL",
                ["orders", "archive_orders"])),
        new(
            "nested-cte",
            new BodyVariant(
                "WITH nested AS (SELECT id FROM orders) SELECT id FROM nested",
                "WITH nested",
                ["orders"])),
        new(
            "postgres-expression-stack",
            new BodyVariant(
                "SELECT id::bigint AS id FROM orders WHERE name ILIKE 'a%' AND created_at > CURRENT_TIMESTAMP - INTERVAL '1 day'",
                "ILIKE",
                ["orders"]))
    ];

    private static readonly GrammarVariant<RootVariant>[] Roots =
    [
        new(
            "select",
            new RootVariant(
                "SELECT id FROM recent",
                "FROM recent",
                [])),
        new(
            "physical-join",
            new RootVariant(
                "SELECT recent.id FROM recent JOIN users u ON recent.id = u.id",
                "JOIN users",
                ["users"])),
        new(
            "correlated-subquery",
            new RootVariant(
                "SELECT id FROM recent WHERE EXISTS (SELECT id FROM users u WHERE u.id = recent.id)",
                "EXISTS",
                ["users"])),
        new(
            "root-set-operation",
            new RootVariant(
                "SELECT id FROM recent UNION ALL SELECT id FROM users",
                "UNION ALL",
                ["users"]))
    ];

    private static readonly GrammarVariant<TailVariant>[] Tails =
    [
        new(
            "none",
            new TailVariant("", null)),
        new(
            "order",
            new TailVariant(" ORDER BY id", "ORDER BY")),
        new(
            "limit",
            new TailVariant(" ORDER BY id LIMIT 10", "LIMIT")),
        new(
            "limit-offset",
            new TailVariant(" ORDER BY id LIMIT 10 OFFSET 2", "OFFSET"))
    ];

    public static IEnumerable<object[]> PostgresCteGrammarMatrix()
    {
        foreach (var (cteForm, body, root, tail) in
                 SyntaxGrammarMatrix.Product(CteForms, Bodies, Roots, Tails))
        {
            var cte = cteForm.Value(body.Value.Sql);
            var sql = cte + " " + root.Value.Sql + tail.Value.Sql;
            var expectedTables = body.Value.PhysicalTables
                .Concat(root.Value.PhysicalTables)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            yield return
            [
                $"{cteForm.Name}__{body.Name}__{root.Name}__{tail.Name}",
                sql,
                body.Value.RenderedFragment,
                root.Value.RenderedFragment,
                tail.Value.RenderedFragment,
                string.Join(",", expectedTables)
            ];
        }
    }

    [Fact]
    public void PostgresCteGrammarMatrix_IsCartesianAndCollisionFree()
    {
        var cases = PostgresCteGrammarMatrix().ToArray();
        var expectedCount =
            CteForms.Length *
            Bodies.Length *
            Roots.Length *
            Tails.Length;

        Assert.Equal(432, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[1]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(PostgresCteGrammarMatrix))]
    public void PostgresCteGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string sql,
        string bodyRenderedFragment,
        string rootRenderedFragment,
        string? tailRenderedFragment,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);
        var facts = SqlCoreInspection.GetQueryFacts(parsed);
        var expectedTables = expectedTablesCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(facts.ContainsCte, name);
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

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "postgres-combinatorial-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            bodyRenderedFragment,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            rootRenderedFragment,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);

        if (tailRenderedFragment is not null)
        {
            Assert.Contains(
                tailRenderedFragment,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__postgres-expression-stack__", StringComparison.Ordinal))
        {
            Assert.Contains("ILIKE", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("INTERVAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__filter-window__", StringComparison.Ordinal))
        {
            Assert.Contains("OVER", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FILTER", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
