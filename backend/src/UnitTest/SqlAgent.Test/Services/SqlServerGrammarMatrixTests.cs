using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlServerGrammarMatrixTests
{
    private sealed record BodyVariant(
        string Sql,
        string RenderedMarker,
        string[] PhysicalTables);

    private sealed record RootVariant(
        string Sql,
        string RenderedMarker,
        string[] PhysicalTables);

    private sealed record PagingVariant(
        Func<string, string> Apply,
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
                "SELECT id FROM orders WHERE active = 1",
                "WHERE",
                ["orders"])),
        new(
            "join-on",
            new BodyVariant(
                "SELECT o.id AS id FROM orders o JOIN customers c ON o.customer_id = c.id",
                "JOIN",
                ["orders", "customers"])),
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
            "bracket-identifiers",
            new BodyVariant(
                "SELECT [id] AS id FROM [orders]",
                "orders",
                ["orders"])),
        new(
            "dateadd",
            new BodyVariant(
                "SELECT id FROM orders WHERE created_at < DATEADD(DAY, 1, created_at)",
                "DATEADD",
                ["orders"])),
        new(
            "datediff",
            new BodyVariant(
                "SELECT id FROM orders WHERE DATEDIFF(DAY, created_at, completed_at) >= 0",
                "DATEDIFF",
                ["orders"])),
        new(
            "nvarchar-max",
            new BodyVariant(
                "SELECT id FROM orders WHERE CAST(name AS NVARCHAR(MAX)) <> ''",
                "NVARCHAR(MAX)",
                ["orders"]))
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
                ["users"]))
    ];

    private static readonly GrammarVariant<PagingVariant>[] Paging =
    [
        new(
            "none",
            new PagingVariant(
                query => query,
                null)),
        new(
            "top",
            new PagingVariant(
                query => $"SELECT TOP 5 id FROM ({query}) limited",
                "TOP")),
        new(
            "offset",
            new PagingVariant(
                query => query + " ORDER BY id OFFSET 5 ROWS",
                "OFFSET")),
        new(
            "offset-fetch",
            new PagingVariant(
                query => query + " ORDER BY id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
                "FETCH NEXT"))
    ];

    public static IEnumerable<object[]> SqlServerCteGrammarMatrix()
    {
        foreach (var (cteForm, body, root, paging) in
                 SyntaxGrammarMatrix.Product(CteForms, Bodies, Roots, Paging))
        {
            var rootSql = paging.Value.Apply(root.Value.Sql);
            var sql = cteForm.Value(body.Value.Sql) + " " + rootSql;
            var expectedTablesCsv = SyntaxGrammarMatrix.ExpectedTables(
                body.Value.PhysicalTables,
                root.Value.PhysicalTables);

            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    cteForm.Name,
                    body.Name,
                    root.Name,
                    paging.Name),
                sql,
                body.Value.RenderedMarker,
                root.Value.RenderedMarker,
                paging.Value.RenderedMarker,
                expectedTablesCsv
            ];
        }
    }

    [Fact]
    public void SqlServerCteGrammarMatrix_IsCartesianAndCollisionFree()
    {
        var cases = SqlServerCteGrammarMatrix().ToArray();
        var expectedCount = SyntaxGrammarMatrix.ProductCount(
            CteForms,
            Bodies,
            Roots,
            Paging);

        Assert.Equal(528, expectedCount);
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
    [MemberData(nameof(SqlServerCteGrammarMatrix))]
    public void SqlServerCteGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string sql,
        string bodyRenderedMarker,
        string rootRenderedMarker,
        string? pagingRenderedMarker,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MsSqlServer);
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
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext(
                "sqlserver-combinatorial-grammar-matrix-v1"),
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

        if (pagingRenderedMarker is not null)
        {
            Assert.Contains(
                pagingRenderedMarker,
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

        if (name.Contains("__bracket-identifiers__", StringComparison.Ordinal))
        {
            Assert.Contains("[orders]", command.Sql, StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__dateadd__", StringComparison.Ordinal))
        {
            Assert.Contains("DATEADD", command.Sql, StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__datediff__", StringComparison.Ordinal))
        {
            Assert.Contains("DATEDIFF", command.Sql, StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__nvarchar-max__", StringComparison.Ordinal))
        {
            Assert.Contains("NVARCHAR(MAX)", command.Sql, StringComparison.OrdinalIgnoreCase);
        }

        if (name.EndsWith("__top", StringComparison.Ordinal))
        {
            Assert.Contains(
                command.Parameters,
                parameter => Convert.ToInt64(parameter.Value) == 5L);
        }

        if (name.EndsWith("__offset-fetch", StringComparison.Ordinal))
        {
            Assert.Contains(
                command.Parameters,
                parameter => Convert.ToInt64(parameter.Value) == 5L);
            Assert.Contains(
                command.Parameters,
                parameter => Convert.ToInt64(parameter.Value) == 10L);
        }
    }
}
