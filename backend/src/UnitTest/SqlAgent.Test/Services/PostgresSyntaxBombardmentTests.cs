using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresSyntaxBombardmentTests
{
    public static IEnumerable<object[]> SupportedPostgresSyntax()
    {
        // Keep this corpus biased toward syntax that was already exercised by the pre-rewrite main tests.
        yield return Case(
            "cte-simple",
            "WITH recent AS (SELECT id FROM orders) SELECT id FROM recent",
            "WITH",
            "orders");
        yield return Case(
            "cte-column-alias",
            "WITH recent(id) AS (SELECT id FROM orders) SELECT id FROM recent",
            "WITH",
            "orders");
        yield return Case(
            "cte-multiple",
            "WITH a AS (SELECT id FROM alpha), b AS (SELECT id FROM beta) SELECT a.id FROM a JOIN b ON a.id = b.id",
            "WITH",
            "alpha,beta");
        yield return Case(
            "cte-chained",
            "WITH a AS (SELECT id FROM alpha), b AS (SELECT id FROM a) SELECT id FROM b",
            "WITH",
            "alpha");
        yield return Case(
            "cte-set-body",
            "WITH x AS (SELECT id FROM alpha UNION ALL SELECT id FROM beta) SELECT id FROM x",
            "UNION ALL",
            "alpha,beta");
        yield return Case(
            "cte-visible-to-root-set-branch",
            "WITH recent AS (SELECT id FROM orders) SELECT id FROM recent UNION ALL SELECT id FROM recent",
            "UNION ALL",
            "orders");
        yield return Case(
            "cte-with-physical-join",
            "WITH x AS (SELECT id FROM audit.events) SELECT x.id FROM x JOIN crm.users u ON x.id = u.id",
            "WITH",
            "audit.events,crm.users");
        yield return Case(
            "comment-prefixed-cte",
            "/* audit */ WITH recent AS (SELECT id FROM orders) SELECT id FROM recent",
            "WITH",
            "orders");
        yield return Case(
            "derived-table",
            "SELECT q.id FROM (SELECT id FROM users WHERE active = TRUE) q",
            "SELECT",
            "users");
        yield return Case(
            "in-subquery",
            "SELECT id FROM users WHERE id IN (SELECT user_id FROM orders WHERE active = TRUE)",
            " IN ",
            "users,orders");
        yield return Case(
            "exists-subquery",
            "SELECT id FROM users WHERE EXISTS (SELECT id FROM orders WHERE orders.user_id = users.id)",
            "EXISTS",
            "users,orders");
        yield return Case(
            "comma-from",
            "SELECT a.id FROM alpha a, beta b WHERE a.id = b.id",
            "CROSS JOIN",
            "alpha,beta");
        yield return Case(
            "group-having",
            "SELECT customer_id, SUM(amount) FROM orders GROUP BY customer_id HAVING SUM(amount) > 10",
            "HAVING",
            "orders");
        yield return Case(
            "aggregate-filter-window",
            "SELECT SUM(amount) FILTER (WHERE status = 'open') OVER (PARTITION BY customer_id ORDER BY created_at ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders WHERE id BETWEEN 1 AND 9",
            "FILTER (WHERE",
            "orders");
        yield return Case(
            "ilike",
            "SELECT id FROM users WHERE name ILIKE 'a%'",
            "ILIKE",
            "users");
        yield return Case(
            "not-ilike",
            "SELECT id FROM users WHERE name NOT ILIKE 'a%'",
            "NOT ILIKE",
            "users");
        yield return Case(
            "typed-date",
            "SELECT DATE '2026-08-21' AS report_date FROM events",
            "SELECT",
            "events");
        yield return Case(
            "typed-time",
            "SELECT TIME '09:30:15' AS report_time FROM events",
            "SELECT",
            "events");
        yield return Case(
            "typed-timestamp",
            "SELECT TIMESTAMP '2026-08-21 09:30:15' AS happened_at FROM events",
            "SELECT",
            "events");
        yield return Case(
            "interval-arithmetic",
            "SELECT created_at + INTERVAL '1 day' FROM events",
            "INTERVAL",
            "events");
        yield return Case(
            "current-timestamp-interval",
            "SELECT CURRENT_TIMESTAMP - INTERVAL '1 day' AS shifted FROM orders LIMIT 1",
            "INTERVAL",
            "orders");
        yield return Case(
            "standard-cast",
            "SELECT CAST(amount AS DECIMAL(12,2)) FROM orders",
            "CAST",
            "orders");
        yield return Case(
            "extract-year",
            "SELECT EXTRACT(YEAR FROM created_at) FROM events",
            "EXTRACT",
            "events");
        yield return Case(
            "nulls-last",
            "SELECT amount FROM orders ORDER BY amount DESC NULLS LAST",
            "NULLS LAST",
            "orders");
        yield return Case(
            "limit-offset",
            "SELECT id FROM users ORDER BY id LIMIT 10 OFFSET 5",
            "OFFSET",
            "users");
        yield return Case(
            "union-all",
            "SELECT id FROM alpha UNION ALL SELECT id FROM beta",
            "UNION ALL",
            "alpha,beta");
        yield return Case(
            "intersect",
            "SELECT id FROM alpha INTERSECT SELECT id FROM beta",
            "INTERSECT",
            "alpha,beta");
        yield return Case(
            "except",
            "SELECT id FROM alpha EXCEPT SELECT id FROM beta",
            "EXCEPT",
            "alpha,beta");
        yield return Case(
            "dollar-quoted-string",
            "SELECT $$O'Brien$$ AS value FROM users",
            "SELECT",
            "users");
        yield return Case(
            "tagged-dollar-quoted-string",
            "SELECT $tag$line 1 '2'$tag$ AS value FROM users",
            "SELECT",
            "users");
        yield return Case(
            "postgres-escape-string",
            "SELECT E'line\\n2' AS value FROM users",
            "SELECT",
            "users");
        yield return Case(
            "quoted-identifiers",
            "SELECT \"Order\".\"Value\" FROM \"Order\"",
            "SELECT",
            "Order");
        yield return Case(
            "expression-valued-in-list",
            "SELECT id FROM users WHERE id IN (other_id, ABS(1))",
            " IN ",
            "users");
    }

    public static IEnumerable<object[]> UnsupportedPostgresSyntax()
    {
        // These were explicit fail-closed boundaries before the F# rewrite and must stay rejected.
        yield return Reject(
            "recursive-cte",
            "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x");
        yield return Reject(
            "join-using",
            "SELECT a.id FROM a JOIN b USING (id)");
        yield return Reject(
            "lateral-source",
            "SELECT q.id FROM LATERAL (SELECT id FROM users) q");
        yield return Reject(
            "fetch-with-ties",
            "SELECT id FROM users ORDER BY id FETCH FIRST 10 ROWS WITH TIES");
        yield return Reject(
            "intersect-all",
            "SELECT id FROM alpha INTERSECT ALL SELECT id FROM beta");
        yield return Reject(
            "except-all",
            "SELECT id FROM alpha EXCEPT ALL SELECT id FROM beta");
        yield return Reject(
            "postgres-comma-limit",
            "SELECT id FROM users LIMIT 5, 10");
        yield return Reject(
            "postgres-top",
            "SELECT TOP 1 id FROM users");
        yield return Reject(
            "unsupported-extract-unit",
            "SELECT EXTRACT(HOUR FROM created_at) FROM events");
        yield return Reject(
            "empty-cte-column-alias-list",
            "WITH x() AS (SELECT id FROM users) SELECT id FROM x");
        yield return Reject(
            "missing-cte-as",
            "WITH x (SELECT id FROM users) SELECT id FROM x");
        yield return Reject(
            "multiple-statements",
            "SELECT id FROM users; SELECT id FROM orders");
        yield return Reject(
            "unbound-colon-parameter",
            "SELECT id FROM users WHERE id = :id");
        yield return Reject(
            "unbound-dollar-parameter",
            "SELECT id FROM users WHERE id = $1");
        yield return Reject(
            "unterminated-string",
            "SELECT 'unterminated FROM users");
    }

    [Theory]
    [MemberData(nameof(SupportedPostgresSyntax))]
    public void SupportedPostgresSyntax_ParsesBindsCompilesAndRenders(
        string name,
        string sql,
        string expectedRenderedFragment,
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

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-positive-syntax-bombardment-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.True(
            command.Sql.Contains(expectedRenderedFragment, StringComparison.OrdinalIgnoreCase),
            $"{name} compiled but lost expected syntax/semantics fragment '{expectedRenderedFragment}'. Rendered SQL: {command.Sql}");
    }

    [Theory]
    [MemberData(nameof(UnsupportedPostgresSyntax))]
    public void UnsupportedPostgresSyntax_RemainsFailClosed(string name, string sql)
    {
        var error = Assert.Throws<SqlParseException>(
            () => CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres));

        Assert.False(string.IsNullOrWhiteSpace(error.Message), name);
    }

    private static object[] Case(
        string name,
        string sql,
        string expectedRenderedFragment,
        string expectedTablesCsv) =>
        [name, sql, expectedRenderedFragment, expectedTablesCsv];

    private static object[] Reject(string name, string sql) => [name, sql];
}
