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
            "trailing-semicolon-select",
            "SELECT id FROM users;",
            "SELECT",
            "users");
        yield return Case(
            "trailing-semicolon-cte",
            "WITH recent AS (SELECT id FROM orders) SELECT id FROM recent;",
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
            "correlated-exists-alias",
            "SELECT u.id FROM users u WHERE EXISTS (SELECT o.id FROM orders o WHERE o.user_id = u.id)",
            "EXISTS",
            "users,orders");
        yield return Case(
            "correlated-shadowing-local",
            "SELECT u.id FROM users u WHERE EXISTS (SELECT u.id FROM orders u WHERE u.user_id = u.id)",
            "EXISTS",
            "users,orders");
        yield return Case(
            "correlated-exists-with-cte",
            "SELECT u.id FROM users u WHERE EXISTS (WITH recent AS (SELECT user_id FROM orders) SELECT user_id FROM recent WHERE user_id = u.id)",
            "WITH",
            "users,orders");
        yield return Case(
            "comma-from",
            "SELECT a.id FROM alpha a, beta b WHERE a.id = b.id",
            "CROSS JOIN",
            "alpha,beta");
        yield return Case(
            "left-outer-join",
            "SELECT a.id FROM alpha a LEFT OUTER JOIN beta b ON a.id = b.id",
            "LEFT JOIN",
            "alpha,beta");
        yield return Case(
            "right-outer-join",
            "SELECT a.id FROM alpha a RIGHT OUTER JOIN beta b ON a.id = b.id",
            "RIGHT JOIN",
            "alpha,beta");
        yield return Case(
            "full-outer-join",
            "SELECT a.id FROM alpha a FULL OUTER JOIN beta b ON a.id = b.id",
            "FULL OUTER JOIN",
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
            "lag-window-frame",
            "SELECT LAG(amount) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) FROM orders",
            "LAG",
            "orders");
        yield return Case(
            "round-avg",
            "SELECT ROUND(AVG(amount), 2) FROM orders",
            "ROUND",
            "orders");
        yield return Case(
            "coalesce",
            "SELECT COALESCE(customer_id, 0) FROM orders",
            "COALESCE",
            "orders");
        yield return Case(
            "ilike",
            "SELECT id FROM users WHERE name ILIKE 'a%'",
            "ILIKE",
            "users");
        yield return Case(
            "not-ilike",
            "SELECT id FROM users WHERE name NOT ILIKE 'a%'",
            "ILIKE",
            "users");
        yield return Case(
            "typed-date",
            "SELECT DATE '2026-08-21' AS report_date FROM events",
            "SELECT",
            "events");
        yield return Case(
            "bare-time-column",
            "SELECT time FROM events",
            "time",
            "events");
        yield return Case(
            "typed-time",
            "SELECT TIME '09:30:15' AS report_time FROM events",
            "SELECT",
            "events");
        yield return Case(
            "time-without-time-zone",
            "SELECT TIME WITHOUT TIME ZONE '09:30:15' AS report_time FROM events",
            "SELECT",
            "events");
        yield return Case(
            "bare-timestamp-column",
            "SELECT timestamp FROM events",
            "timestamp",
            "events");
        yield return Case(
            "typed-timestamp",
            "SELECT TIMESTAMP '2026-08-21 09:30:15' AS happened_at FROM events",
            "SELECT",
            "events");
        yield return Case(
            "leading-decimal-literal",
            "SELECT .5 FROM users",
            "SELECT",
            "users");
        yield return Case(
            "trailing-decimal-literal",
            "SELECT 1. FROM users",
            "SELECT",
            "users");
        yield return Case(
            "scientific-literal",
            "SELECT 1e2 FROM users",
            "SELECT",
            "users");
        yield return Case(
            "scientific-negative-exponent-literal",
            "SELECT 1E-2 FROM users",
            "SELECT",
            "users");
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
            "current-date",
            "SELECT CURRENT_DATE AS value FROM orders",
            "CURRENT_DATE",
            "orders");
        yield return Case(
            "current-time",
            "SELECT CURRENT_TIME AS value FROM orders",
            "CURRENT_TIME",
            "orders");
        yield return Case(
            "current-timestamp",
            "SELECT CURRENT_TIMESTAMP AS value FROM orders",
            "CURRENT_TIMESTAMP",
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
            "nulls-first",
            "SELECT amount FROM orders ORDER BY amount ASC NULLS FIRST",
            "NULLS FIRST",
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
            "select-all",
            "SELECT ALL id FROM users",
            "SELECT",
            "users");
        yield return Case(
            "union-distinct-explicit",
            "SELECT id FROM alpha UNION DISTINCT SELECT id FROM beta",
            "UNION",
            "alpha,beta");
        yield return Case(
            "intersect",
            "SELECT id FROM alpha INTERSECT SELECT id FROM beta",
            "INTERSECT",
            "alpha,beta");
        yield return Case(
            "intersect-distinct-explicit",
            "SELECT id FROM alpha INTERSECT DISTINCT SELECT id FROM beta",
            "INTERSECT",
            "alpha,beta");
        yield return Case(
            "except",
            "SELECT id FROM alpha EXCEPT SELECT id FROM beta",
            "EXCEPT",
            "alpha,beta");
        yield return Case(
            "except-distinct-explicit",
            "SELECT id FROM alpha EXCEPT DISTINCT SELECT id FROM beta",
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
            "nonreserved-zone-column",
            "SELECT zone FROM events",
            "zone",
            "events");
        yield return Case(
            "nonreserved-conflict-column",
            "SELECT conflict FROM events",
            "conflict",
            "events");
        yield return Case(
            "legacy-key-column",
            "SELECT key FROM events",
            "key",
            "events");
        yield return Case(
            "legacy-excluded-column",
            "SELECT excluded FROM events",
            "excluded",
            "events");
        yield return Case(
            "legacy-percent-column",
            "SELECT percent FROM events",
            "percent",
            "events");
        yield return Case(
            "nonreserved-delete-column",
            "SELECT delete FROM events",
            "delete",
            "events");
        yield return Case(
            "nonreserved-update-column",
            "SELECT update FROM events",
            "update",
            "events");
        yield return Case(
            "nonreserved-insert-column",
            "SELECT insert FROM events",
            "insert",
            "events");
        yield return Case(
            "nonreserved-values-column",
            "SELECT values FROM events",
            "values",
            "events");
        yield return Case(
            "nonreserved-escape-column",
            "SELECT escape FROM events",
            "escape",
            "events");
        yield return Case(
            "nonreserved-nothing-column",
            "SELECT nothing FROM events",
            "nothing",
            "events");
        yield return Case(
            "nonreserved-next-column",
            "SELECT next FROM events",
            "next",
            "events");
        yield return Case(
            "nonreserved-ties-column",
            "SELECT ties FROM events",
            "ties",
            "events");
        yield return Case(
            "nonreserved-within-column",
            "SELECT within FROM events",
            "within",
            "events");
        yield return Case(
            "nonreserved-without-column",
            "SELECT without FROM events",
            "without",
            "events");
        yield return Case(
            "ordinary-top-column",
            "SELECT top FROM events",
            "top",
            "events");
        yield return Case(
            "ordinary-duplicate-column",
            "SELECT duplicate FROM events",
            "duplicate",
            "events");
        yield return Case(
            "ordinary-matching-column",
            "SELECT matching FROM events",
            "matching",
            "events");
        yield return Case(
            "ordinary-separator-column",
            "SELECT separator FROM events",
            "separator",
            "events");
        yield return Case(
            "contextual-projection-alias-zone",
            "SELECT id zone FROM events",
            "zone",
            "events");
        yield return Case(
            "contextual-projection-alias-conflict",
            "SELECT id conflict FROM events",
            "conflict",
            "events");
        yield return Case(
            "contextual-projection-alias-date",
            "SELECT id date FROM events",
            "date",
            "events");
        yield return Case(
            "contextual-table-alias-zone",
            "SELECT zone.id FROM events zone",
            "zone",
            "events");
        yield return Case(
            "contextual-derived-alias-zone",
            "SELECT zone.id FROM (SELECT id FROM events) AS zone",
            "zone",
            "events");
        yield return Case(
            "cte-wildcard",
            "WITH recent AS (SELECT * FROM orders) SELECT * FROM recent",
            "WITH",
            "orders");
        yield return Case(
            "cte-order-limit",
            "WITH recent AS (SELECT id FROM orders ORDER BY id DESC LIMIT 5) SELECT id FROM recent ORDER BY id",
            "LIMIT",
            "orders");
        yield return Case(
            "cte-aggregate",
            "WITH totals AS (SELECT customer_id, SUM(amount) AS total FROM orders GROUP BY customer_id) SELECT total FROM totals WHERE total > 10",
            "SUM",
            "orders");
        yield return Case(
            "count-star",
            "SELECT COUNT(*) FROM users",
            "COUNT",
            "users");
        yield return Case(
            "count-distinct",
            "SELECT COUNT(DISTINCT id) FROM users",
            "DISTINCT",
            "users");
        yield return Case(
            "count-all",
            "SELECT COUNT(ALL id) FROM users",
            "COUNT",
            "users");
        yield return Case(
            "string-agg-inline-order",
            "SELECT STRING_AGG(name, ',' ORDER BY name DESC) FROM users",
            "ORDER BY",
            "users");
        yield return Case(
            "searched-case",
            "SELECT CASE WHEN status = 'open' THEN 1 ELSE 0 END FROM orders",
            "CASE",
            "orders");
        yield return Case(
            "simple-case",
            "SELECT CASE status WHEN 'open' THEN 1 WHEN 'closed' THEN 2 ELSE 0 END FROM orders",
            "CASE",
            "orders");
        yield return Case(
            "is-not-null",
            "SELECT id FROM users WHERE deleted_at IS NOT NULL",
            "IS NOT NULL",
            "users");
        yield return Case(
            "not-in",
            "SELECT id FROM users WHERE id NOT IN (1, 2, 3)",
            "NOT IN",
            "users");
        yield return Case(
            "signed-in-list",
            "SELECT id FROM users WHERE id IN (-1, +2, 3.5, -4.25, 'x', NULL, TRUE, FALSE)",
            " IN ",
            "users");
        yield return Case(
            "signed-not-in-list",
            "SELECT id FROM users WHERE id NOT IN (-10, +20)",
            "NOT IN",
            "users");
        yield return Case(
            "concat",
            "SELECT first_name || last_name FROM users",
            "||",
            "users");
        yield return Case(
            "postfix-cast",
            "SELECT created_at::date FROM events",
            "CAST",
            "events");
        yield return Case(
            "standard-multiword-cast",
            "SELECT CAST(created_at AS TIMESTAMP(6) WITHOUT TIME ZONE) FROM events",
            "CAST",
            "events");
        yield return Case(
            "postfix-multiword-cast",
            "SELECT created_at::TIMESTAMP(6) WITHOUT TIME ZONE FROM events",
            "CAST",
            "events");
        yield return Case(
            "cast-in-having",
            "SELECT customer_id, MAX(created_at) FROM events GROUP BY customer_id HAVING MAX(created_at)::date > DATE '2026-01-01'",
            "HAVING",
            "events");
        yield return Case(
            "fetch-first",
            "SELECT id FROM users ORDER BY id FETCH FIRST 5 ROWS ONLY",
            "LIMIT",
            "users");
        yield return Case(
            "fetch-first-without-order",
            "SELECT id FROM users FETCH FIRST 5 ROWS ONLY",
            "LIMIT",
            "users");
        yield return Case(
            "fetch-first-without-from",
            "SELECT 1 FETCH FIRST 1 ROW ONLY",
            "LIMIT",
            "");
        yield return Case(
            "offset-fetch",
            "SELECT id FROM users ORDER BY id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            "OFFSET",
            "users");
        yield return Case(
            "limit-all-offset",
            "SELECT id FROM users ORDER BY id LIMIT ALL OFFSET 5",
            "OFFSET",
            "users");
        yield return Case(
            "expression-valued-in-list",
            "SELECT id FROM users WHERE id IN (other_id, ABS(1))",
            " IN ",
            "users");
    }

    public static IEnumerable<object[]> SupportedPostgresParserSyntax()
    {
        // The pre-rewrite Core parser accepted these shapes even when a later stage could still reject semantics.
        yield return ParserCase(
            "aggregate-all-count",
            "SELECT COUNT(ALL id) FROM users");
        yield return ParserCase(
            "aggregate-all-sum",
            "SELECT SUM(ALL amount) FROM orders");
        yield return ParserCase(
            "aggregate-all-string-agg",
            "SELECT STRING_AGG(ALL name, ',') FROM users");
        yield return ParserCase(
            "schema-qualified-standard-cast-type",
            "SELECT CAST(amount AS pg_catalog.numeric) FROM orders");
        yield return ParserCase(
            "schema-qualified-postfix-cast-type",
            "SELECT amount::pg_catalog.numeric FROM orders");
        yield return ParserCase(
            "time-keyword-function-form",
            "SELECT TIME(created_at) FROM events");
        yield return ParserCase(
            "timestamp-keyword-function-form",
            "SELECT TIMESTAMP(created_at) FROM events");
        yield return ParserCase(
            "legacy-complex-cte-cast-having",
            @"WITH SystemMax AS (
                SELECT MAX(order_date) AS max_system_date FROM orders
            )
            SELECT
                c.customer_id,
                c.company_name,
                c.contact_name,
                c.phone,
                sm.max_system_date,
                MAX(o.order_date) AS last_order_date,
                (sm.max_system_date::date - MAX(o.order_date)::date) AS days_since_last_order
            FROM customers c
            LEFT JOIN orders o ON c.customer_id = o.customer_id
            CROSS JOIN SystemMax sm
            GROUP BY
                c.customer_id,
                c.company_name,
                c.contact_name,
                c.phone,
                sm.max_system_date
            HAVING
                (sm.max_system_date::date - MAX(o.order_date)::date) > 180
                OR MAX(o.order_date) IS NULL
            ORDER BY days_since_last_order DESC;");
    }

    public static IEnumerable<object[]> ExplicitFailClosedPostgresSyntax()
    {
        yield return RejectWithMessage(
            "recursive-cte",
            "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x",
            "WITH RECURSIVE");
        yield return RejectWithMessage(
            "join-using",
            "SELECT a.id FROM a JOIN b USING (id)",
            "USING");
        yield return RejectWithMessage(
            "cross-join-using",
            "SELECT a.id FROM a CROSS JOIN b USING (id)",
            "ON/USING");
        yield return RejectWithMessage(
            "lateral-source",
            "SELECT q.id FROM LATERAL (SELECT id FROM users) q",
            "LATERAL");
        yield return RejectWithMessage(
            "natural-join",
            "SELECT a.id FROM a NATURAL JOIN b",
            "NATURAL JOIN");
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
            "chained-comparison",
            "SELECT id FROM users WHERE id = 1 = 1");
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

    [Fact]
    public void ExplicitTimestampTimezoneIntent_PreservesLegacyClrType()
    {
        var withZone = CoreSqlTextParser.ParseQuery(
            "SELECT TIMESTAMP WITH TIME ZONE '2026-08-21T09:30:00+08:00' FROM events",
            SqlAgentToolType.Postgres);
        var withoutZone = CoreSqlTextParser.ParseQuery(
            "SELECT TIMESTAMP WITHOUT TIME ZONE '2026-08-21 09:30:00' FROM events",
            SqlAgentToolType.Postgres);

        var withZoneSelect = Assert.IsType<SelectStatement>(withZone.Statement);
        var withoutZoneSelect = Assert.IsType<SelectStatement>(withoutZone.Statement);

        var offset = Assert.IsType<LiteralExpr>(Assert.Single(withZoneSelect.Select).Expression);
        var local = Assert.IsType<LiteralExpr>(Assert.Single(withoutZoneSelect.Select).Expression);

        Assert.IsType<SqlOffsetDateTimeValue>(offset.Value);
        Assert.IsType<SqlLocalDateTimeValue>(local.Value);
    }

    [Fact]
    public void PositiveIntegerLiteral_PreservesLegacyClrType()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT 1 FROM users",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var literal = Assert.IsType<LiteralExpr>(Assert.Single(select.Select).Expression);
        Assert.IsType<int>(literal.Value);
        Assert.Equal(1, literal.Value);
    }

    [Fact]
    public void NegativeIntegerLiteral_PreservesLegacyClrType()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT -1 FROM users",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var literal = Assert.IsType<LiteralExpr>(Assert.Single(select.Select).Expression);
        Assert.IsType<int>(literal.Value);
        Assert.Equal(-1, literal.Value);
    }

    [Fact]
    public void LargeIntegerLiteral_PreservesLegacyDecimalClrType()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT 2147483648 FROM users",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var literal = Assert.IsType<LiteralExpr>(Assert.Single(select.Select).Expression);
        Assert.IsType<decimal>(literal.Value);
        Assert.Equal(2147483648m, literal.Value);
    }

    [Fact]
    public void IntegerLiteral_CompileParametersPreserveLegacyClrTypes()
    {
        var small = SqlCoreFacade.CompileQuery(
            "SELECT -1 FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-integer-type-parity-v1"),
            new SqlExecutionPlanPolicy());

        var large = SqlCoreFacade.CompileQuery(
            "SELECT 2147483648 FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-integer-type-parity-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains(small.Parameters, parameter =>
            parameter.Value is int value && value == -1);
        Assert.Contains(large.Parameters, parameter =>
            parameter.Value is decimal value && value == 2147483648m);
    }

    [Fact]
    public void PrefixNot_PreservesLegacyPredicatePrecedence()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users WHERE NOT id = 1",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var unary = Assert.IsType<UnaryExpr>(select.Where);
        Assert.Equal("NOT", unary.Operator, ignoreCase: true);

        var comparison = Assert.IsType<BinaryExpr>(unary.Operand);
        Assert.Equal("=", comparison.Operator);
    }

    [Fact]
    public void ConcatAndAddition_PreserveLegacyLeftAssociativePrecedence()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT first_name || suffix + 1 FROM users",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var root = Assert.IsType<BinaryExpr>(Assert.Single(select.Select).Expression);
        Assert.Equal("+", root.Operator);

        var concat = Assert.IsType<BinaryExpr>(root.Left);
        Assert.Equal("||", concat.Operator);
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

        if (name == "not-ilike")
            Assert.Contains("NOT", command.Sql, StringComparison.OrdinalIgnoreCase);
        if (name == "fetch-first")
            Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 5));
    }

    [Theory]
    [MemberData(nameof(SupportedPostgresParserSyntax))]
    public void SupportedPostgresParserSyntax_RemainsAccepted(string name, string sql)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        Assert.NotNull(parsed.Statement);
        Assert.False(string.IsNullOrWhiteSpace(parsed.RawSql), name);
    }

    [Theory]
    [MemberData(nameof(ExplicitFailClosedPostgresSyntax))]
    public void ExplicitFailClosedPostgresSyntax_ReportsTheIntendedGrammarBoundary(
        string name,
        string sql,
        string expectedDiagnostic)
    {
        var error = Assert.Throws<SqlParseException>(
            () => CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres));

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
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

    private static object[] ParserCase(string name, string sql) => [name, sql];

    private static object[] RejectWithMessage(
        string name,
        string sql,
        string expectedDiagnostic) =>
        [name, sql, expectedDiagnostic];

    private static object[] Reject(string name, string sql) => [name, sql];
}
