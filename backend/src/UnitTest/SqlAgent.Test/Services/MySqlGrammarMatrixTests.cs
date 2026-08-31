using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class MySqlGrammarMatrixTests
{
    private sealed record BodyVariant(
        string Sql,
        string RenderedMarker,
        string[] PhysicalTables);

    private sealed record RootVariant(
        string Sql,
        string RenderedMarker,
        string[] PhysicalTables);

    private sealed record TailVariant(
        string Sql,
        string? RenderedMarker);

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
                "orders",
                ["orders"])),
        new(
            "where",
            new BodyVariant(
                "SELECT id FROM orders WHERE active = TRUE",
                "WHERE",
                ["orders"])),
        new(
            "join-on",
            new BodyVariant(
                "SELECT o.id AS id FROM orders o JOIN customers c ON o.customer_id = c.id",
                "JOIN",
                ["orders", "customers"])),
        new(
            "join-using",
            new BodyVariant(
                "SELECT id FROM orders JOIN archived_orders USING (id)",
                "USING",
                ["orders", "archived_orders"])),
        new(
            "group-having",
            new BodyVariant(
                "SELECT customer_id AS id FROM orders GROUP BY customer_id HAVING SUM(amount) > 0",
                "HAVING",
                ["orders"])),
        new(
            "window",
            new BodyVariant(
                "SELECT ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY created_at) AS id FROM orders",
                "ROW_NUMBER",
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
                "nested",
                ["orders"])),
        new(
            "backtick-identifiers",
            new BodyVariant(
                "SELECT `id` AS id FROM `orders`",
                "orders",
                ["orders"])),
        new(
            "mysql-expression-stack",
            new BodyVariant(
                "SELECT CAST(id AS SIGNED) AS id FROM orders WHERE DATE(created_at) = DATE(created_at)",
                "CAST(",
                ["orders"])),
        new(
            "ordered-group-concat",
            new BodyVariant(
                "SELECT GROUP_CONCAT(name ORDER BY created_at DESC SEPARATOR '|') AS id FROM users",
                "GROUP_CONCAT(",
                ["users"]))
    ];

    private static readonly GrammarVariant<RootVariant>[] Roots =
    [
        new(
            "select",
            new RootVariant(
                "SELECT id FROM recent",
                "recent",
                [])),
        new(
            "physical-join",
            new RootVariant(
                "SELECT recent.id FROM recent JOIN users u ON recent.id = u.id",
                "JOIN",
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
                ["users"])),
        new(
            "root-join-using",
            new RootVariant(
                "SELECT id FROM recent JOIN users USING (id)",
                "USING",
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
            new TailVariant(" ORDER BY id LIMIT 10 OFFSET 2", "OFFSET")),
        new(
            "comma-limit",
            new TailVariant(" ORDER BY id LIMIT 2, 10", "LIMIT"))
    ];

    public static IEnumerable<object[]> MySqlCteGrammarMatrix()
    {
        foreach (var (cteForm, body, root, tail) in
                 SyntaxGrammarMatrix.Product(CteForms, Bodies, Roots, Tails))
        {
            var sql =
                cteForm.Value(body.Value.Sql) +
                " " +
                root.Value.Sql +
                tail.Value.Sql;
            var expectedTables = body.Value.PhysicalTables
                .Concat(root.Value.PhysicalTables)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    cteForm.Name,
                    body.Name,
                    root.Name,
                    tail.Name),
                sql,
                body.Value.RenderedMarker,
                root.Value.RenderedMarker,
                tail.Value.RenderedMarker,
                string.Join(",", expectedTables)
            ];
        }
    }

    [Fact]
    public void MySqlCteGrammarMatrix_IsCartesianAndCollisionFree()
    {
        var cases = MySqlCteGrammarMatrix().ToArray();
        var expectedCount = SyntaxGrammarMatrix.ProductCount(
            CteForms,
            Bodies,
            Roots,
            Tails);

        Assert.Equal(900, expectedCount);
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
    [MemberData(nameof(MySqlCteGrammarMatrix))]
    public void MySqlCteGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string sql,
        string bodyRenderedMarker,
        string rootRenderedMarker,
        string? tailRenderedMarker,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MySQL);
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
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext(
                "mysql-combinatorial-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recent", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            bodyRenderedMarker,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            rootRenderedMarker,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);

        if (tailRenderedMarker is not null)
        {
            Assert.Contains(
                tailRenderedMarker,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }

        foreach (var table in expectedTables)
        {
            Assert.Contains(
                table,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__backtick-identifiers__", StringComparison.Ordinal))
        {
            Assert.Contains("`orders`", command.Sql, StringComparison.Ordinal);
        }

        if (name.Contains("__mysql-expression-stack__", StringComparison.Ordinal))
        {
            Assert.Contains("CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__ordered-group-concat__", StringComparison.Ordinal))
        {
            Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SEPARATOR '|'", command.Sql, StringComparison.Ordinal);
        }

        if (name.EndsWith("__comma-limit", StringComparison.Ordinal))
        {
            Assert.Contains(
                command.Parameters,
                parameter => Convert.ToInt64(parameter.Value) == 10L);
            Assert.Contains(
                command.Parameters,
                parameter => Convert.ToInt64(parameter.Value) == 2L);
        }
    }
}
