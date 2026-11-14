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
/// Real PostgreSQL 16 physical pg_constraint + backing pg_class index parity.
/// SqlCore analyzes without a connection; test executes isolated rollback DDL.
/// </summary>
public sealed class SqlCorePostgresRenameConstraintLiveParityTests
{
    private sealed record Key(string Owner, string Name, string Kind, string Target,
        string Index, string[] Local, string[] Remote);
    private sealed record Scenario(string Name, string Sql);
    private static readonly Scenario[] Scenarios =
    [
        new("parent_primary_key_and_inbound_fk",
            "CREATE TABLE app.parent(id INTEGER NOT NULL,CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.child(pid INTEGER,CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.parent RENAME CONSTRAINT pk_parent TO pk_new;" +
            "ALTER TABLE app.child RENAME CONSTRAINT fk_child TO fk_new;"),
        new("unique_rename_chain",
            "CREATE TABLE app.t(id INTEGER,code INTEGER,CONSTRAINT uq_old UNIQUE(code));" +
            "ALTER TABLE app.t RENAME CONSTRAINT uq_old TO uq_middle;" +
            "ALTER TABLE app.t RENAME CONSTRAINT uq_middle TO uq_final;" +
            "ALTER TABLE app.t DROP CONSTRAINT uq_final;"),
        new("same_old_fk_name_on_two_tables",
            "CREATE TABLE app.parent(id INTEGER NOT NULL,CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.a(pid INTEGER,CONSTRAINT fk_same FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "CREATE TABLE app.b(pid INTEGER,CONSTRAINT fk_same FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.a RENAME CONSTRAINT fk_same TO fk_other;" +
            "ALTER TABLE app.b RENAME CONSTRAINT fk_same TO fk_a_new;"),
        new("rename_then_drop_fk_and_parent",
            "CREATE TABLE app.parent(id INTEGER NOT NULL,CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.child(pid INTEGER,CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.child RENAME CONSTRAINT fk_child TO fk_new;" +
            "ALTER TABLE app.child DROP CONSTRAINT fk_new;" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent;"),
        new("composite_unique_and_remote_fk",
            "CREATE TABLE app.parent(a INTEGER NOT NULL,b INTEGER NOT NULL,CONSTRAINT uq_pair UNIQUE(a,b));" +
            "CREATE TABLE app.child(x INTEGER,y INTEGER,CONSTRAINT fk_pair FOREIGN KEY(x,y) REFERENCES app.parent(a,b));" +
            "ALTER TABLE app.parent RENAME CONSTRAINT uq_pair TO uq_new_pair;" +
            "ALTER TABLE app.child RENAME CONSTRAINT fk_pair TO fk_new_pair;")
    ];
    public static IEnumerable<object[]> Cases => Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void FiveDistinctPgCatalogWorkflows() =>
        Assert.Equal(5, Scenarios.Select(s => s.Sql).Distinct().Count());

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRenameMatchesPhysicalConstraintAndImplicitIndexNames(string id)
    {
        var scenario = Scenarios.Single(s => s.Name == id);
        var model = SqlCoreFacade.AnalyzeScript(
            scenario.Sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            null, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));

        await using var db = new NpgsqlConnection(ConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var schema = "sqlcore_rc_" + Guid.NewGuid().ToString("N");
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        for (var i = 0; i < model.Statements.Count; i++)
        {
            var step = model.Statements[i];
            Assert.True(step.CatalogAfter.IsClosedWorld, id + "#" + i);
            await Execute(db, tx, step.Syntax.Text.Replace(
                "app.", schema + ".", StringComparison.Ordinal));
            var actual = await ReadKeys(db, tx, schema);
            var expected = step.CatalogAfter.Tables.SelectMany(t => t.Constraints.Select(c =>
                new Key(t.QualifiedName!.Parts[^1], c.Name, c.Kind switch
                {
                    SqlCatalogConstraintKind.PrimaryKey => "p",
                    SqlCatalogConstraintKind.Unique => "u",
                    SqlCatalogConstraintKind.ForeignKey => "f",
                    _ => throw new InvalidOperationException("Unexpected key kind")
                }, c.Kind == SqlCatalogConstraintKind.ForeignKey
                    ? c.ReferencedIdentity!.Parts[^1] : "",
                   c.Kind == SqlCatalogConstraintKind.ForeignKey ? "" : c.Name,
                   c.Columns.ToArray(), c.ReferencedColumns.ToArray())))
                .OrderBy(x => x.Owner, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Length, actual.Length);
            for (var j = 0; j < expected.Length; j++)
            {
                Assert.Equal(expected[j].Owner, actual[j].Owner);
                Assert.Equal(expected[j].Name, actual[j].Name);
                Assert.Equal(expected[j].Kind, actual[j].Kind);
                Assert.Equal(expected[j].Target, actual[j].Target);
                Assert.Equal(expected[j].Index, actual[j].Index);
                Assert.Equal(expected[j].Local, actual[j].Local);
                Assert.Equal(expected[j].Remote, actual[j].Remote);
            }
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Requires TEST_POSTGRES_CONNECTION in CI.");
        Assert.Skip("Requires real PostgreSQL and TEST_POSTGRES_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task Execute(NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Key[]> ReadKeys(NpgsqlConnection db, NpgsqlTransaction tx, string schema)
    {
        const string sql = """
            SELECT owner.relname, c.conname, c.contype::text,
                   CASE WHEN c.contype='f' THEN parent.relname ELSE '' END,
                   CASE WHEN c.contype IN ('p','u') THEN idx.relname ELSE '' END,
                   ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY col(n,pos)
                         JOIN pg_catalog.pg_attribute a ON a.attrelid=owner.oid AND a.attnum=col.n
                         ORDER BY col.pos),
                   CASE WHEN c.contype='f' THEN
                     ARRAY(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY col(n,pos)
                           JOIN pg_catalog.pg_attribute a ON a.attrelid=parent.oid AND a.attnum=col.n
                           ORDER BY col.pos)
                   ELSE ARRAY[]::name[] END
            FROM pg_catalog.pg_constraint c
            JOIN pg_catalog.pg_class owner ON owner.oid=c.conrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid=owner.relnamespace
            LEFT JOIN pg_catalog.pg_class parent ON parent.oid=c.confrelid
            LEFT JOIN pg_catalog.pg_class idx ON idx.oid=c.conindid
            WHERE ns.nspname=@schema AND c.contype IN ('p','u','f')
            ORDER BY owner.relname,c.conname
            """;
        var keys = new List<Key>();
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            keys.Add(new Key(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetFieldValue<string[]>(5), reader.GetFieldValue<string[]>(6)));
        return keys.ToArray();
    }
}
