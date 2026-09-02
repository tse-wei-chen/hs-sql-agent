using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class OracleGrammarMatrixTests
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
        string? RenderedMarker,
        CanonicalPagingExpectation Canonical);

    private static readonly GrammarVariant<Func<string, string>>[] CteForms =
    [
        new("single", body => $"WITH recent AS ({body})"),
        new("column-alias", body => $"WITH recent(id) AS ({body})"),
        new("multiple-chained", body => $"WITH base AS ({body}), recent(id) AS (SELECT id FROM base)")
    ];

    private static readonly GrammarVariant<BodyVariant>[] Bodies =
    [
        new("simple", new BodyVariant(
            "SELECT id FROM orders", "orders", ["orders"])),
        new("where", new BodyVariant(
            "SELECT id FROM orders WHERE active = 1", "WHERE", ["orders"])),
        new("join-on", new BodyVariant(
            "SELECT o.id AS id FROM orders o JOIN customers c ON o.customer_id = c.id",
            "JOIN", ["orders", "customers"])),
        new("join-using", new BodyVariant(
            "SELECT id FROM orders JOIN archive_orders USING (id)",
            "USING", ["orders", "archive_orders"])),
        new("group-having", new BodyVariant(
            "SELECT customer_id AS id FROM orders GROUP BY customer_id HAVING SUM(amount) > 0",
            "HAVING", ["orders"])),
        new("window", new BodyVariant(
            "SELECT ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY created_at) AS id FROM orders",
            "ROW_NUMBER", ["orders"])),
        new("subquery", new BodyVariant(
            "SELECT id FROM orders WHERE EXISTS (SELECT id FROM audit_events WHERE audit_events.order_id = orders.id)",
            "EXISTS", ["orders", "audit_events"])),
        new("set-operation", new BodyVariant(
            "SELECT id FROM orders UNION ALL SELECT id FROM archive_orders",
            "UNION ALL", ["orders", "archive_orders"])),
        new("quoted-identifiers", new BodyVariant(
            "SELECT \"ID\" AS id FROM \"ORDERS\"",
            "ORDERS", ["ORDERS"])),
        new("typed-date", new BodyVariant(
            "SELECT DATE '2026-08-24' AS id FROM dual",
            "dual", ["dual"])),
        new("typed-timestamp", new BodyVariant(
            "SELECT TIMESTAMP '2026-08-24 12:34:56' AS id FROM dual",
            "dual", ["dual"])),
        new("sysdate", new BodyVariant(
            "SELECT SYSDATE AS id FROM dual",
            "SYSDATE", ["dual"]))
    ];

    private static readonly GrammarVariant<RootVariant>[] Roots =
    [
        new("select", new RootVariant(
            "SELECT id FROM recent", "recent", [])),
        new("physical-join", new RootVariant(
            "SELECT recent.id FROM recent JOIN users u ON recent.id = u.id",
            "JOIN", ["users"])),
        new("correlated-subquery", new RootVariant(
            "SELECT id FROM recent WHERE EXISTS (SELECT id FROM users u WHERE u.id = recent.id)",
            "EXISTS", ["users"])),
        new("root-set-operation", new RootVariant(
            "SELECT id FROM recent UNION ALL SELECT id FROM users",
            "UNION ALL", ["users"])),
        new("root-join-using", new RootVariant(
            "SELECT id FROM recent JOIN users USING (id)",
            "USING", ["users"]))
    ];

    private static readonly GrammarVariant<TailVariant>[] Tails =
    [
        new("none", new TailVariant(
            "", null, new CanonicalPagingExpectation(null, null))),
        new("order", new TailVariant(
            " ORDER BY id", "ORDER BY", new CanonicalPagingExpectation(null, null))),
        new("offset", new TailVariant(
            " ORDER BY id OFFSET 5 ROWS", "OFFSET", new CanonicalPagingExpectation(null, 5))),
        new("fetch-first", new TailVariant(
            " ORDER BY id FETCH FIRST 10 ROWS ONLY",
            "FETCH NEXT", new CanonicalPagingExpectation(10, null))),
        new("offset-fetch", new TailVariant(
            " ORDER BY id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            "FETCH NEXT", new CanonicalPagingExpectation(10, 5)))
    ];

    public static IEnumerable<object?[]> OracleCteGrammarMatrix()
    {
        foreach (var (cteForm, body, root, tail) in
                 SyntaxGrammarMatrix.Product(CteForms, Bodies, Roots, Tails))
        {
            var sql = cteForm.Value(body.Value.Sql) + " " + root.Value.Sql + tail.Value.Sql;
            var expectedTablesCsv = SyntaxGrammarMatrix.ExpectedTables(
                body.Value.PhysicalTables,
                root.Value.PhysicalTables);

            yield return
            [
                SyntaxGrammarMatrix.CaseName(cteForm.Name, body.Name, root.Name, tail.Name),
                sql,
                body.Value.RenderedMarker,
                root.Value.RenderedMarker,
                tail.Value.RenderedMarker,
                tail.Value.Canonical.Limit,
                tail.Value.Canonical.Offset,
                expectedTablesCsv
            ];
        }
    }

    [Fact]
    public void OracleCteGrammarMatrix_IsCartesianAndCollisionFree()
    {
        var cases = OracleCteGrammarMatrix().ToArray();
        var expectedCount = SyntaxGrammarMatrix.ProductCount(CteForms, Bodies, Roots, Tails);

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
    [MemberData(nameof(OracleCteGrammarMatrix))]
    public void OracleCteGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string sql,
        string bodyRenderedMarker,
        string rootRenderedMarker,
        string? tailRenderedMarker,
        int? expectedLimit,
        int? expectedOffset,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle);
        var (actualLimit, actualOffset) =
            SyntaxGrammarMatrix.CanonicalPaging(parsed.Statement, name);
        Assert.Equal(expectedLimit, actualLimit);
        Assert.Equal(expectedOffset, actualOffset);

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
                actual => string.Equals(actual, table, StringComparison.OrdinalIgnoreCase));
        }

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("oracle-combinatorial-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recent", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(bodyRenderedMarker, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rootRenderedMarker, command.Sql, StringComparison.OrdinalIgnoreCase);

        if (tailRenderedMarker is not null)
            Assert.Contains(tailRenderedMarker, command.Sql, StringComparison.OrdinalIgnoreCase);

        foreach (var table in expectedTables)
            Assert.Contains(table, command.Sql, StringComparison.OrdinalIgnoreCase);

        if (name.Contains("__quoted-identifiers__", StringComparison.Ordinal))
            Assert.Contains("\"ORDERS\"", command.Sql, StringComparison.Ordinal);

        if (name.Contains("__typed-date__", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("2026-08-24", command.Sql, StringComparison.Ordinal);
            Assert.Contains(
                command.Parameters,
                parameter => parameter.Value is DateTime value
                    && value.Date == new DateTime(2026, 8, 24));
        }

        if (name.Contains("__typed-timestamp__", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("2026-08-24 12:34:56", command.Sql, StringComparison.Ordinal);
            Assert.Contains(
                command.Parameters,
                parameter => parameter.Value is DateTime value
                    && value == new DateTime(2026, 8, 24, 12, 34, 56));
        }

        if (name.Contains("__sysdate__", StringComparison.Ordinal))
            Assert.Contains("SYSDATE", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (name.EndsWith("__offset", StringComparison.Ordinal))
            Assert.Contains(command.Parameters, parameter => SyntaxGrammarMatrix.IntegerParameterEquals(parameter.Value, 5L));

        if (name.EndsWith("__fetch-first", StringComparison.Ordinal))
        {
            Assert.Contains(command.Parameters, parameter => SyntaxGrammarMatrix.IntegerParameterEquals(parameter.Value, 0L));
            Assert.Contains(command.Parameters, parameter => SyntaxGrammarMatrix.IntegerParameterEquals(parameter.Value, 10L));
        }

        if (name.EndsWith("__offset-fetch", StringComparison.Ordinal))
        {
            Assert.Contains(command.Parameters, parameter => SyntaxGrammarMatrix.IntegerParameterEquals(parameter.Value, 5L));
            Assert.Contains(command.Parameters, parameter => SyntaxGrammarMatrix.IntegerParameterEquals(parameter.Value, 10L));
        }
    }
}
