using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Independent live PostgreSQL view rewrite-rule pg_depend oracle. Verifies
/// each immutable Catalog transition against physical attnum-based dependency
/// names, including ones used only in JOIN/WHERE, and persistent View headers.
/// </summary>
public sealed class SqlCorePostgresRenameViewLiveParityTests
{
    private sealed record Source(string View, string Relation, string Column);
    private sealed record Output(string View, string Column, string Type, bool Nullable);
    private sealed record Scenario(string Name, string Script);

    private static readonly Scenario[] Scenarios =
    [
        new("direct_alias",
            "CREATE TABLE src(id INTEGER, label TEXT);" +
            "CREATE VIEW v (public_id) AS SELECT id FROM src;" +
            "ALTER TABLE src RENAME COLUMN id TO new_id;"),
        new("chain_aliased",
            "CREATE TABLE src(id INTEGER, label TEXT);" +
            "CREATE VIEW v1 (external_id) AS SELECT id FROM src;" +
            "CREATE VIEW v2 AS SELECT external_id FROM v1;" +
            "ALTER TABLE src RENAME COLUMN id TO physical_id;" +
            "CREATE VIEW v3 AS SELECT external_id FROM v2;"),
        new("join_where",
            "CREATE TABLE orders(id INTEGER NOT NULL, title TEXT, status TEXT);" +
            "CREATE TABLE lines(id INTEGER, order_id INTEGER, amount INTEGER);" +
            "CREATE VIEW joined (display_title, total) AS SELECT o.title, l.amount " +
            "FROM orders o JOIN lines l ON o.id = l.order_id WHERE o.status = 'ready';" +
            "ALTER TABLE orders RENAME COLUMN id TO order_key;" +
            "ALTER TABLE orders RENAME COLUMN status TO state;" +
            "ALTER TABLE lines RENAME COLUMN order_id TO order_ref;"),
        new("fk_and_view",
            "CREATE TABLE parent(id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child(pid INTEGER, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));" +
            "CREATE VIEW v (public_id) AS SELECT id FROM parent;" +
            "ALTER TABLE parent RENAME COLUMN id TO renamed_id;"),
        new("star_projection",
            "CREATE TABLE src(id INTEGER, label TEXT);" +
            "CREATE VIEW v AS SELECT * FROM src;" +
            "ALTER TABLE src RENAME COLUMN id TO renamed_id;" +
            "CREATE VIEW child AS SELECT id FROM v;")
    ];

    public static IEnumerable<object[]> Cases => Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void FiveDifferentLiveViewProofScripts()
    {
        Assert.Equal(5, Scenarios.Length);
        Assert.Equal(5, Scenarios.Select(s => s.Script).Distinct().Count());
        Assert.Equal(5, Scenarios.Select(s => s.Name).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ViewSourceAttnumProvenanceAndPublicHeadersMatchPostgresAfterEveryDdl(string id)
    {
        var script = Scenarios.Single(s => s.Name == id).Script;
        var model = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            null, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));

        await using var db = new NpgsqlConnection(ConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var schema = "sqlcore_view_" + Guid.NewGuid().ToString("N");
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        await Execute(db, tx, "SET LOCAL search_path TO " + schema);
        for (var index = 0; index < model.Statements.Count; index++)
        {
            var statement = model.Statements[index];
            Assert.True(statement.CatalogAfter.IsClosedWorld, id + "#" + index);
            await Execute(db, tx, statement.Syntax.Text);

            // pg_rewrite -> pg_depend -> pg_attribute is independent of SQL
            // deparsing and tracks dependency by physical attribute number.
            var observedSources = await ReadSources(db, tx, schema);
            var expectedSources = statement.CatalogAfter.Views
                .SelectMany(v => v.SourceBindings.SelectMany(b =>
                    b.Columns.Select(c => new Source(
                        v.QualifiedName.Parts[^1],
                        b.Relation.Parts[^1], c))))
                .Distinct()
                .OrderBy(s => s.View, StringComparer.Ordinal)
                .ThenBy(s => s.Relation, StringComparer.Ordinal)
                .ThenBy(s => s.Column, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedSources, observedSources);

            var observedOutputs = await ReadOutputs(db, tx, schema);
            var expectedOutputs = statement.CatalogAfter.Views
                .SelectMany(v => v.Columns.Select(c =>
                    new Output(v.QualifiedName.Parts[^1], c.Name, c.TypeName,
                               c.Nullable)))
                .OrderBy(o => o.View, StringComparer.Ordinal)
                .ThenBy(o => o.Column, StringComparer.Ordinal).ToArray();
            // PostgreSQL information_schema.views is authoritative on
            // output column names and types; nullability is not guaranteed
            // to reflect strict source-column NOT NULL in View metadata.
            Assert.Equal(expectedOutputs.Select(o => (o.View, o.Column, o.Type)),
                observedOutputs.Select(o => (o.View, o.Column, o.Type)));
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                          StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PostgreSQL live view parity requires TEST_POSTGRES_CONNECTION.");
        Assert.Skip("Requires live PostgreSQL service.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task Execute(NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Source[]> ReadSources(
        NpgsqlConnection db, NpgsqlTransaction tx, string schema)
    {
        const string sql = """
            SELECT view_rel.relname, source_rel.relname, attribute.attname
            FROM pg_catalog.pg_rewrite rule
            JOIN pg_catalog.pg_class view_rel ON view_rel.oid = rule.ev_class
            JOIN pg_catalog.pg_namespace view_ns ON view_ns.oid = view_rel.relnamespace
            JOIN pg_catalog.pg_depend dependency ON
                dependency.classid = 'pg_catalog.pg_rewrite'::regclass
                AND dependency.objid = rule.oid
                AND dependency.refclassid = 'pg_catalog.pg_class'::regclass
                AND dependency.refobjsubid > 0
            JOIN pg_catalog.pg_class source_rel ON source_rel.oid = dependency.refobjid
            JOIN pg_catalog.pg_attribute attribute ON
                attribute.attrelid = source_rel.oid AND attribute.attnum = dependency.refobjsubid
            WHERE view_ns.nspname = @schema
              AND view_rel.relkind = 'v'
              AND rule.rulename = '_RETURN'
            GROUP BY view_rel.relname, source_rel.relname, attribute.attname
            ORDER BY view_rel.relname, source_rel.relname, attribute.attname
            """;
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        var values = new List<Source>();
        await using var rows = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rows.ReadAsync(TestContext.Current.CancellationToken))
            values.Add(new Source(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        return values.ToArray();
    }

    private static async Task<Output[]> ReadOutputs(
        NpgsqlConnection db, NpgsqlTransaction tx, string schema)
    {
        const string sql = """
            SELECT column_name, table_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND table_name IN (
                 SELECT table_name FROM information_schema.views WHERE table_schema = @schema)
            ORDER BY table_name, column_name
            """;
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        var values = new List<Output>();
        await using var rows = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rows.ReadAsync(TestContext.Current.CancellationToken))
            values.Add(new Output(rows.GetString(1), rows.GetString(0),
                rows.GetString(2), rows.GetString(3) == "YES"));
        return values.ToArray();
    }
}
