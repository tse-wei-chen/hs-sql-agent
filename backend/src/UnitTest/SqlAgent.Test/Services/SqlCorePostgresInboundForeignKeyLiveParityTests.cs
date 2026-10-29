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
/// PostgreSQL 16 physical pg_constraint conkey/confkey differential proof.
/// Test harness executes SQL; SqlCore's AnalyzeScript remains entirely static.
/// </summary>
public sealed class SqlCorePostgresInboundForeignKeyLiveParityTests
{
    private sealed record ForeignKey(string Table, string Name, string Target,
        string[] LocalColumns, string[] ReferencedColumns);
    private sealed record Scenario(string Name, string Sql);

    private static readonly Scenario[] Scenarios =
    [
        new("single_inbound",
            "CREATE TABLE parent (id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (parent_id INTEGER, CONSTRAINT fk_child FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO new_id;"),
        new("two_children_composite",
            "CREATE TABLE parent (a INTEGER NOT NULL, b INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(a,b));" +
            "CREATE TABLE child_a (fa INTEGER, fb INTEGER, CONSTRAINT fk_a FOREIGN KEY(fa,fb) REFERENCES parent(a,b));" +
            "CREATE TABLE child_b (ga INTEGER, gb INTEGER, CONSTRAINT fk_b FOREIGN KEY(ga,gb) REFERENCES parent(a,b));" +
            "ALTER TABLE parent RENAME COLUMN b TO renamed_b;"),
        new("self_fk",
            "CREATE TABLE tree (id INTEGER NOT NULL, parent_id INTEGER, " +
            "CONSTRAINT pk_tree PRIMARY KEY(id), CONSTRAINT fk_tree FOREIGN KEY(parent_id) REFERENCES tree(id));" +
            "ALTER TABLE tree RENAME COLUMN id TO node_id;" +
            "ALTER TABLE tree RENAME COLUMN parent_id TO ancestor_id;"),
        new("rename_chain",
            "CREATE TABLE parent (id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (parent_id INTEGER, CONSTRAINT fk_child FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO intermediate;" +
            "ALTER TABLE parent RENAME COLUMN intermediate TO final_id;")
    ];

    public static IEnumerable<object[]> Cases =>
        Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void CorpusHasFourIndependentCrossObjectWorkflows()
    {
        Assert.Equal(4, Scenarios.Select(x => x.Name).Distinct().Count());
        Assert.Equal(4, Scenarios.Select(x => x.Sql).Distinct().Count());
        Assert.All(Scenarios, x => Assert.True(x.Sql.Count(c => c == ';') >= 3));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryStatementMatchesPhysicalPostgresForeignKeyColumnLineage(string id)
    {
        var sql = Scenarios.Single(x => x.Name == id).Sql;
        var modeled = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);
        Assert.Empty(modeled.Diagnostics);
        Assert.All(modeled.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));

        await using var db = new NpgsqlConnection(ConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var schema = "sqlcore_fk_" + Guid.NewGuid().ToString("N");
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        await Execute(db, tx, "SET LOCAL search_path TO " + schema);
        for (var i = 0; i < modeled.Statements.Count; i++)
        {
            var statement = modeled.Statements[i];
            Assert.True(statement.CatalogAfter.IsClosedWorld, id + "#" + i);
            await Execute(db, tx, statement.Syntax.Text);
            var actual = await ReadForeignKeys(db, tx, schema);
            var expected = statement.CatalogAfter.Tables
                .SelectMany(table => table.Constraints
                    .Where(fk => fk.Kind == SqlCatalogConstraintKind.ForeignKey)
                    .Select(fk => new ForeignKey(
                        table.QualifiedName.Parts[^1], fk.Name,
                        fk.ReferencedIdentity.Parts[^1],
                        fk.Columns.ToArray(), fk.ReferencedColumns.ToArray())))
                .OrderBy(x => x.Table, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Length, actual.Count);
            for (var j = 0; j < expected.Length; j++)
            {
                Assert.Equal(expected[j].Table, actual[j].Table);
                Assert.Equal(expected[j].Name, actual[j].Name);
                Assert.Equal(expected[j].Target, actual[j].Target);
                Assert.Equal(expected[j].LocalColumns, actual[j].LocalColumns);
                Assert.Equal(expected[j].ReferencedColumns, actual[j].ReferencedColumns);
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
            throw new InvalidOperationException(
                "TEST_POSTGRES_CONNECTION required for PostgreSQL FK rename parity in CI.");
        Assert.Skip("Requires real PostgreSQL service and TEST_POSTGRES_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task Execute(NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<ForeignKey>> ReadForeignKeys(
        NpgsqlConnection db, NpgsqlTransaction tx, string schema)
    {
        const string sql = """
            SELECT child.relname, c.conname, parent.relname,
                ARRAY(SELECT a.attname
                      FROM unnest(c.conkey) WITH ORDINALITY AS key(attnum, pos)
                      JOIN pg_catalog.pg_attribute a
                        ON a.attrelid = child.oid AND a.attnum = key.attnum
                      ORDER BY key.pos),
                ARRAY(SELECT a.attname
                      FROM unnest(c.confkey) WITH ORDINALITY AS key(attnum, pos)
                      JOIN pg_catalog.pg_attribute a
                        ON a.attrelid = parent.oid AND a.attnum = key.attnum
                      ORDER BY key.pos)
            FROM pg_catalog.pg_constraint c
            JOIN pg_catalog.pg_class child ON child.oid = c.conrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid = child.relnamespace
            JOIN pg_catalog.pg_class parent ON parent.oid = c.confrelid
            WHERE ns.nspname = @schema AND c.contype = 'f'
            ORDER BY child.relname, c.conname
            """;
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        var result = new List<ForeignKey>();
        await using var rows = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rows.ReadAsync(TestContext.Current.CancellationToken))
            result.Add(new ForeignKey(rows.GetString(0), rows.GetString(1),
                rows.GetString(2), rows.GetFieldValue<string[]>(3),
                rows.GetFieldValue<string[]>(4)));
        return result;
    }
}
