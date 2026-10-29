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
/// Independent PostgreSQL pg_constraint/pg_attribute differential oracle for
/// DROP CONSTRAINT CASCADE, RESTRICT and IF EXISTS. Every DDL is checked against
/// the prior immutable Catalog in a rollback-only isolated schema.
/// </summary>
public sealed class SqlCorePostgresDropConstraintLiveParityTests
{
    private sealed record Key(string Owner, string Name, string Kind,
        string Target, string[] Local, string[] Remote);
    private sealed record Scenario(string Name, string Script);

    private static readonly Scenario[] Scenarios =
    [
        new("cascade_two_children",
            "CREATE TABLE app.parent(id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.child_a(pid INTEGER, CONSTRAINT fk_a FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "CREATE TABLE app.child_b(pid INTEGER, CONSTRAINT fk_b FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent CASCADE;"),
        new("composite_then_drop_column",
            "CREATE TABLE app.parent(a INTEGER NOT NULL, b INTEGER NOT NULL, CONSTRAINT pk_pair PRIMARY KEY(a,b));" +
            "CREATE TABLE app.child(x INTEGER,y INTEGER,CONSTRAINT fk_pair FOREIGN KEY(x,y) REFERENCES app.parent(a,b));" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_pair CASCADE;" +
            "ALTER TABLE app.parent DROP COLUMN b;"),
        new("self_fk_and_unrelated_unique",
            "CREATE TABLE app.tree(id INTEGER NOT NULL, parent_id INTEGER, code INTEGER NOT NULL, " +
            "CONSTRAINT pk_tree PRIMARY KEY(id), CONSTRAINT uq_code UNIQUE(code)," +
            "CONSTRAINT fk_tree FOREIGN KEY(parent_id) REFERENCES app.tree(id));" +
            "ALTER TABLE app.tree DROP CONSTRAINT pk_tree CASCADE;"),
        new("fk_only_and_optional_absent",
            "CREATE TABLE app.parent(id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.child(pid INTEGER, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.child DROP CONSTRAINT fk_child CASCADE;" +
            "ALTER TABLE app.child DROP CONSTRAINT IF EXISTS fk_child RESTRICT;" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent RESTRICT;"),
        new("specific_unique_other_fk_survives",
            "CREATE TABLE app.parent(id INTEGER NOT NULL, code INTEGER NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(id), CONSTRAINT uq_code UNIQUE(code));" +
            "CREATE TABLE app.child_a(pid INTEGER, CONSTRAINT fk_id FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "CREATE TABLE app.child_b(code INTEGER, CONSTRAINT fk_code FOREIGN KEY(code) REFERENCES app.parent(code));" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent CASCADE;")
    ];

    public static IEnumerable<object[]> Cases => Scenarios.Select(x => new object[] { x.Name });

    [Fact]
    public void CorpusContainsFiveDistinctMultiStepWorkflows()
    {
        Assert.Equal(5, Scenarios.Length);
        Assert.Equal(5, Scenarios.Select(s => s.Script).Distinct().Count());
        Assert.All(Scenarios, s => Assert.True(s.Script.Count(c => c == ';') >= 2));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryTransitionMatchesRealPgConstraintGraph(string id)
    {
        var script = Scenarios.Single(s => s.Name == id).Script;
        var model = SqlCoreFacade.AnalyzeScript(script, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty(), null, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));

        await using var db = new NpgsqlConnection(ConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var schema = "sqlcore_cascade_" + Guid.NewGuid().ToString("N");
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        for (var i = 0; i < model.Statements.Count; i++)
        {
            var step = model.Statements[i];
            Assert.True(step.CatalogAfter.IsClosedWorld, id + "#" + i);
            await Execute(db, tx, step.Syntax.Text.Replace(
                "app.", schema + ".", StringComparison.Ordinal));
            var actual = await ReadKeys(db, tx, schema);
            var expected = step.CatalogAfter.Tables.SelectMany(t =>
                    t.Constraints.Select(c => new Key(
                        t.QualifiedName!.Parts[^1], c.Name,
                        c.Kind switch
                        {
                            SqlCatalogConstraintKind.PrimaryKey => "p",
                            SqlCatalogConstraintKind.Unique => "u",
                            SqlCatalogConstraintKind.ForeignKey => "f",
                            _ => throw new InvalidOperationException("Unsupported modeled key kind")
                        },
                        c.Kind == SqlCatalogConstraintKind.ForeignKey
                            ? c.ReferencedIdentity!.Parts[^1] : "",
                        c.Columns.ToArray(), c.ReferencedColumns.ToArray())))
                .OrderBy(k => k.Owner, StringComparer.Ordinal)
                .ThenBy(k => k.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Length, actual.Count);
            for (var k = 0; k < expected.Length; k++)
            {
                Assert.Equal((expected[k].Owner, expected[k].Name, expected[k].Kind, expected[k].Target),
                    (actual[k].Owner, actual[k].Name, actual[k].Kind, actual[k].Target));
                Assert.Equal(expected[k].Local, actual[k].Local);
                Assert.Equal(expected[k].Remote, actual[k].Remote);
            }
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                          StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required in CI.");
        Assert.Skip("Requires real PostgreSQL and TEST_POSTGRES_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task Execute(NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<Key>> ReadKeys(
        NpgsqlConnection db, NpgsqlTransaction tx, string schema)
    {
        const string sql = """
            SELECT owner.relname, c.conname, c.contype::text,
                   CASE WHEN c.contype='f' THEN parent.relname ELSE '' END,
                   ARRAY(SELECT attr.attname
                         FROM unnest(c.conkey) WITH ORDINALITY x(n,pos)
                         JOIN pg_catalog.pg_attribute attr
                           ON attr.attrelid=owner.oid AND attr.attnum=x.n
                         ORDER BY x.pos),
                   CASE WHEN c.contype='f' THEN
                       ARRAY(SELECT attr.attname
                             FROM unnest(c.confkey) WITH ORDINALITY x(n,pos)
                             JOIN pg_catalog.pg_attribute attr
                               ON attr.attrelid=parent.oid AND attr.attnum=x.n
                             ORDER BY x.pos)
                   ELSE ARRAY[]::name[] END
            FROM pg_catalog.pg_constraint c
            JOIN pg_catalog.pg_class owner ON owner.oid=c.conrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid=owner.relnamespace
            LEFT JOIN pg_catalog.pg_class parent ON parent.oid=c.confrelid
            WHERE ns.nspname=@schema AND c.contype IN ('p','u','f')
            ORDER BY owner.relname,c.conname
            """;
        var result = new List<Key>();
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            result.Add(new Key(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<string[]>(4), reader.GetFieldValue<string[]>(5)));
        return result;
    }
}
