using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real InnoDB oracle: a secondary index disjoint from FK left-prefix
/// must not erase/replace the physical supporting index, and DROP FOREIGN KEY
/// must not become an invented proof that its index disappeared.
/// </summary>
public sealed class SqlCoreMySqlForeignKeySupportingIndexLiveParityTests
{
    private sealed record Case(string Id, string Action, bool DropExplicit, bool DropForeign);
    private sealed record Physical(string Name, string Column, int Position, int NonUnique);

    private static readonly Case[] Cases =
    [
        new("create_index_disjoint", "CREATE INDEX ix_note ON child(note)", false, false),
        new("alter_add_key_disjoint", "ALTER TABLE child ADD KEY ix_note(note)", false, false),
        new("secondary_index_roundtrip", "CREATE INDEX ix_note ON child(note)", true, false),
        new("drop_fk_leaves_index_state_observable", "ALTER TABLE child ADD INDEX ix_note(note)", false, true)
    ];

    private const string Graph =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, note INT, " +
        "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));";

    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    public static IEnumerable<object[]> WorkflowIds => Cases.Select(c => new object[] { c.Id });

    [Fact]
    public void FourDistinctRealInnoDbWorkflowsAreDeclared() =>
        Assert.Equal(4, Cases.Select(x => x.Id).Distinct().Count());

    [Theory]
    [MemberData(nameof(WorkflowIds))]
    public async Task DisjointIndexNeverMutatesExistingSupportingIndex(string name)
    {
        var test = Cases.Single(x => x.Id == name);
        var sql = Graph + test.Action + ";" +
            (test.DropExplicit ? "ALTER TABLE child DROP INDEX ix_note;" : "") +
            (test.DropForeign ? "ALTER TABLE child DROP FOREIGN KEY fk_child;" : "");
        var model = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));

        await WithDatabase(async (db, database) =>
        {
            await Execute(db, model.Statements[0].Syntax.Text);
            await Execute(db, model.Statements[1].Syntax.Text);
            var before = await Read(db, database);
            Assert.Contains(before, p => p.Name == "fk_child" && p.Column == "pid");
            Assert.Contains(before, p => p.Name == "PRIMARY" && p.Column == "id");
            await Execute(db, model.Statements[2].Syntax.Text);
            var afterAdd = await Read(db, database);
            Assert.Equal(before, afterAdd.Where(p => p.Name != "ix_note").ToArray());
            Assert.Contains(afterAdd, p => p.Name == "ix_note"
                && p.Column == "note" && p.Position == 1 && p.NonUnique == 1);
            var snapshot = model.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child");
            Assert.Equal("ix_note", Assert.Single(snapshot.Indexes).Name);
            Assert.Equal("fk_child", snapshot.Constraints.Single(c =>
                c.Kind == SqlCatalogConstraintKind.ForeignKey).Name);

            if (test.DropExplicit)
            {
                await Execute(db, model.Statements[3].Syntax.Text);
                Assert.Equal(before, await Read(db, database));
                Assert.Empty(model.FinalCatalog.Tables.Single(t => t.Name == "child").Indexes);
            }
            if (test.DropForeign)
            {
                await Execute(db, model.Statements[3].Syntax.Text);
                var afterDrop = await Read(db, database);
                // Physical-index presence after a DROP FOREIGN KEY is observed,
                // never inferred from the absence of a Catalog FK constraint.
                Assert.Contains(afterDrop, p => p.Name == "ix_note" && p.Column == "note");
                Assert.Empty(model.FinalCatalog.Tables.Single(t => t.Name == "child")
                    .Constraints.Where(c => c.Kind == SqlCatalogConstraintKind.ForeignKey));
            }
        });
    }

    [Fact]
    public async Task ImplicitSupportingIndexNameCollisionIsRejectedByMySql()
    {
        const string attempted = "CREATE INDEX fk_child ON child(note)";
        var staticResult = SqlCoreFacade.AnalyzeScript(Graph + attempted + ";",
            SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty(), Linux,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, staticResult.Statements[2].Status);
        await WithDatabase(async (db, database) =>
        {
            await Execute(db, staticResult.Statements[0].Syntax.Text);
            await Execute(db, staticResult.Statements[1].Syntax.Text);
            var baseline = await Read(db, database);
            await Assert.ThrowsAsync<MySqlException>(async () =>
                await Execute(db, attempted));
            Assert.Equal(baseline, await Read(db, database));
        });
    }

    private static async Task<Physical[]> Read(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX, NON_UNIQUE
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'child'
             ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        var results = new List<Physical>();
        await using var command = new MySqlCommand(sql, db);
        command.Parameters.AddWithValue("@db", database);
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            results.Add(new Physical(reader.GetString(0), reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2)),
                Convert.ToInt32(reader.GetValue(3))));
        return results.ToArray();
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION required in CI.");
        Assert.Skip("Requires real MySQL with TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task WithDatabase(Func<MySqlConnection, string, Task> action)
    {
        var root = ConnectionString();
        var database = "sqlcore_fksidx_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = new MySqlCommand("SELECT @@lower_case_table_names", admin))
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken)));
        await Execute(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var builder = new MySqlConnectionStringBuilder(root)
            {
                Database = database, Pooling = false
            };
            await using var db = new MySqlConnection(builder.ConnectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await action(db, database);
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + database + "`");
        }
    }

    private static async Task Execute(MySqlConnection db, string sql)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
