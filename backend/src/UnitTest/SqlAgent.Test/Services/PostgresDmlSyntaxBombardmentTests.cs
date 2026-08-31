using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresDmlSyntaxBombardmentTests
{
    public static IEnumerable<object[]> SupportedPostgresDml()
    {
        yield return Case(
            "insert-returning",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id, name",
            "RETURNING",
            true);
        yield return Case(
            "insert-on-conflict-do-nothing",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO NOTHING",
            "ON CONFLICT",
            false);
        yield return Case(
            "insert-on-conflict-do-nothing-targetless",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT DO NOTHING",
            "ON CONFLICT DO NOTHING",
            false);
        yield return Case(
            "insert-select-on-conflict-do-nothing-targetless",
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users ON CONFLICT DO NOTHING",
            "ON CONFLICT DO NOTHING",
            false);
        yield return Case(
            "insert-on-conflict-update-returning",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO UPDATE SET name = excluded.name RETURNING id",
            "DO UPDATE",
            true);
        yield return Case(
            "insert-select-on-conflict-do-nothing",
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users ON CONFLICT (id) DO NOTHING",
            "ON CONFLICT",
            false);
        yield return Case(
            "multi-row-conflict-do-nothing",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (1, 'Bob') ON CONFLICT (id) DO NOTHING",
            "DO NOTHING",
            false);
        yield return Case(
            "multi-row-values-null-decimal",
            "INSERT INTO users (id, name, score) VALUES (1, 'A', NULL), (2, 'B', 9.5)",
            "INSERT",
            false);
        yield return Case(
            "update-date-literal",
            "UPDATE orders SET order_date = DATE '2026-08-21', required_date = DATE '2026-08-21' WHERE id = 1",
            "UPDATE",
            false);
        yield return Case(
            "update-time-literal",
            "UPDATE schedules SET starts_at = TIME '09:30:00' WHERE id = 1",
            "UPDATE",
            false);
        yield return Case(
            "update-returning",
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id",
            "RETURNING",
            true);
        yield return Case(
            "update-target-implicit-alias",
            "UPDATE users u SET name = 'Alice' WHERE u.id = 1 RETURNING u.id",
            "RETURNING",
            true);
        yield return Case(
            "update-target-as-alias",
            "UPDATE users AS u SET name = 'Alice' WHERE u.id = 1 RETURNING u.id",
            "RETURNING",
            true);
        yield return Case(
            "delete-target-as-alias",
            "DELETE FROM users AS u WHERE u.id = 1 RETURNING u.id",
            "RETURNING",
            true);
        yield return Case(
            "qualified-returning-column",
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING users.id",
            "RETURNING",
            true);
        yield return Case(
            "mixed-wildcard-returning",
            "DELETE FROM users WHERE id = 1 RETURNING *, id",
            "RETURNING",
            true);
        yield return Case(
            "duplicate-returning-output",
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id, id",
            "RETURNING",
            true);
        yield return Case(
            "update-from",
            "UPDATE users SET status = archived.status FROM archived WHERE users.id = archived.id",
            " FROM ",
            false);
        yield return Case(
            "update-from-source-alias",
            "UPDATE users SET status = a.status FROM archived AS a WHERE users.id = a.id",
            " FROM ",
            false);
        yield return Case(
            "update-from-source-returning",
            "UPDATE users SET status = a.status FROM archived AS a WHERE users.id = a.id RETURNING users.id, a.id AS archived_id",
            "RETURNING",
            true);
        yield return Case(
            "update-from-derived-source",
            "UPDATE users SET status = a.status FROM (SELECT id, status FROM archived) AS a WHERE users.id = a.id",
            " FROM ",
            false);
        yield return Case(
            "delete-using",
            "DELETE FROM inventory USING warehouse WHERE inventory.id = warehouse.inventory_id",
            "USING",
            false);
        yield return Case(
            "delete-using-source-alias",
            "DELETE FROM inventory USING warehouse AS w WHERE inventory.id = w.inventory_id",
            "USING",
            false);
        yield return Case(
            "delete-using-source-returning",
            "DELETE FROM inventory USING warehouse AS w WHERE inventory.id = w.inventory_id RETURNING inventory.id, w.inventory_id AS warehouse_inventory_id",
            "RETURNING",
            true);
        yield return Case(
            "delete-using-derived-source",
            "DELETE FROM inventory USING (SELECT inventory_id FROM warehouse) AS w WHERE inventory.id = w.inventory_id",
            "USING",
            false);
        yield return Case(
            "delete-using-returning",
            "DELETE FROM inventory USING warehouse WHERE inventory.id = warehouse.inventory_id RETURNING id",
            "RETURNING",
            true);
        yield return Case(
            "delete-complex-predicate",
            "DELETE FROM users WHERE (status = 'old' OR status IS NULL) AND id IN (1, 2) AND age BETWEEN 18 AND 65",
            "DELETE",
            false);
        yield return Case(
            "update-string-containing-where",
            "UPDATE main.notes SET body = 'look where the value is', title = 'set, where' WHERE id = 7",
            "UPDATE",
            false);
        yield return Case(
            "quoted-dml-identifiers",
            "UPDATE \"app\".\"Order\" SET \"Display Name\" = 'ready' WHERE \"Id\" = 1",
            "UPDATE",
            false);
    }

    public static IEnumerable<object[]> ParserOnlySupportedPostgresDml()
    {
        // main accepted this raw syntax, while compilation requires explicit source uniqueness assurance.
        yield return ParserCase(
            "insert-select-conflict-update",
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users ON CONFLICT (id) DO UPDATE SET name = excluded.name");
    }

    public static IEnumerable<object[]> BindingFailClosedPostgresDml()
    {
        yield return Reject(
            "update-target-alias-hides-original-name",
            "UPDATE users AS u SET name = 'Alice' WHERE users.id = 1",
            "unknown table/alias qualifier");
        yield return Reject(
            "delete-target-alias-hides-original-name",
            "DELETE FROM users AS u WHERE users.id = 1",
            "unknown table/alias qualifier");
    }

    public static IEnumerable<object[]> ExplicitFailClosedPostgresDml()
    {
        yield return Reject(
            "qualified-update-assignment",
            "UPDATE users SET users.name = 'Alice' WHERE id = 1",
            "unqualified");
        yield return Reject(
            "duplicate-update-assignment",
            "UPDATE users SET name = 'Alice', name = 'Bob' WHERE id = 1",
            "more than once");
        yield return Reject(
            "conflict-targetless-do-update",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT DO UPDATE SET name = excluded.name",
            "explicit conflict target");
        yield return Reject(
            "conflict-arbitrary-expression",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO UPDATE SET name = excluded.name || '!'",
            "conflict clause");
        yield return Reject(
            "duplicate-conflict-target",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id, id) DO NOTHING",
            "more than once");
        yield return Reject(
            "duplicate-conflict-assignment",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO UPDATE SET name = excluded.name, name = excluded.name",
            "more than once");
        yield return Reject(
            "postgres-on-duplicate-key",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON DUPLICATE KEY UPDATE name = name",
            "conflict target");
        yield return Reject(
            "implicit-returning-column-alias",
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id returned_id",
            "requires AS");
        yield return Reject(
            "implicit-returning-expression-alias",
            "DELETE FROM users WHERE id = 1 RETURNING lower(name) normalized_name",
            "requires AS");
        yield return Reject(
            "returning-wildcard-alias",
            "DELETE FROM users WHERE id = 1 RETURNING * AS everything",
            "wildcard");
        yield return Reject(
            "insert-missing-column-list",
            "INSERT INTO users VALUES (1, 'Alice')",
            "INSERT column list");
        yield return Reject(
            "insert-row-width-mismatch",
            "INSERT INTO users (id, name) VALUES (1)",
            "columns were declared");
        yield return Reject(
            "insert-multi-row-width-mismatch",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2)",
            "columns were declared");
        yield return Reject(
            "invalid-date-assignment",
            "UPDATE orders SET order_date = DATE '2026-02-30' WHERE id = 1",
            "Invalid DATE literal");
        yield return Reject(
            "date-without-string-literal",
            "UPDATE orders SET order_date = DATE 20260821 WHERE id = 1",
            "Unexpected trailing token");
        yield return Reject(
            "dml-unbound-parameter",
            "UPDATE users SET name = :name WHERE id = 1",
            "Unbound SQL parameter");
        yield return Reject(
            "dml-unterminated-string",
            "UPDATE users SET name = 'unterminated WHERE id = 1",
            "Unterminated string");
        yield return Reject(
            "dml-multiple-statements",
            "DELETE FROM users; DELETE FROM audit",
            "Unexpected trailing token");
    }

    [Fact]
    public void ExplicitTimestampTimezoneIntent_PreservesLegacyAssignmentTypes()
    {
        var withZone = CoreSqlTextParser.ParseDml(
            "UPDATE events SET occurred_at = TIMESTAMP WITH TIME ZONE '2026-08-21T09:30:00+08:00' WHERE id = 1",
            SqlAgentToolType.Postgres);
        var withoutZone = CoreSqlTextParser.ParseDml(
            "UPDATE events SET occurred_at = TIMESTAMP WITHOUT TIME ZONE '2026-08-21 09:30:00' WHERE id = 1",
            SqlAgentToolType.Postgres);

        var withZoneUpdate = Assert.IsType<UpdateStatement>(withZone.Statement);
        var withoutZoneUpdate = Assert.IsType<UpdateStatement>(withoutZone.Statement);

        var offset = Assert.IsType<LiteralExpr>(Assert.Single(withZoneUpdate.Assignments).Value);
        var local = Assert.IsType<LiteralExpr>(Assert.Single(withoutZoneUpdate.Assignments).Value);

        Assert.IsType<SqlOffsetDateTimeValue>(offset.Value);
        Assert.IsType<SqlLocalDateTimeValue>(local.Value);
    }

    [Theory]
    [MemberData(nameof(SupportedPostgresDml))]
    public void SupportedPostgresDml_ParsesCompilesAndPreservesClause(
        string name,
        string sql,
        string expectedSqlFragment,
        bool returnsRows)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-dml-syntax-bombardment-v1"));

        Assert.Contains(expectedSqlFragment, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(returnsRows, command.ReturnsRows);

        if (name == "update-from")
        {
            Assert.Contains("archived", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("users", command.Sql, StringComparison.OrdinalIgnoreCase);
        }

        if (name.StartsWith("delete-using", StringComparison.Ordinal))
            Assert.Contains("warehouse", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ParserOnlySupportedPostgresDml))]
    public void ParserOnlySupportedPostgresDml_RemainsAccepted(string name, string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);

        Assert.NotNull(parsed.Statement);
        Assert.False(string.IsNullOrWhiteSpace(parsed.RawSql), name);
    }

    [Theory]
    [MemberData(nameof(BindingFailClosedPostgresDml))]
    public void BindingFailClosedPostgresDml_ReportsBindingBoundary(
        string name,
        string sql,
        string expectedDiagnostic)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        Assert.NotNull(parsed.Statement);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("postgres-dml-syntax-bombardment-v1")));

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void ReturningUnknownQualifier_FailsClosedDuringBinding()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING missing.id",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("postgres-dml-syntax-bombardment-v1")));

        Assert.Contains("unknown table/alias qualifier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ExplicitFailClosedPostgresDml))]
    public void ExplicitFailClosedPostgresDml_ReportsIntendedBoundary(
        string name,
        string sql,
        string expectedDiagnostic)
    {
        var error = Assert.Throws<SqlParseException>(
            () => CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres));

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static object[] Case(
        string name,
        string sql,
        string expectedSqlFragment,
        bool returnsRows) =>
        [name, sql, expectedSqlFragment, returnsRows];

    private static object[] ParserCase(string name, string sql) => [name, sql];

    private static object[] Reject(
        string name,
        string sql,
        string expectedDiagnostic) =>
        [name, sql, expectedDiagnostic];
}
