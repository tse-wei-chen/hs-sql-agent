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
/// Real MySQL 8 InnoDB differential: a covering secondary index may silently
/// replace an auto FK-support index; static Catalog must not predict whether
/// the auto index survives. Per-scenario database isolation for implicit DDL commits.
/// </summary>
public sealed class SqlCoreMySqlForeignKeyCoveringIndexLiveParityTests
{
    private sealed record Scenario(string Name, string Add, string[] Columns);
    private sealed record Physical(string Name, string Column, int Ordinal);
    private const string Graph =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, note INT, " +
        "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));";

    private static readonly Scenario[] Scenarios =
    [
        new("single_column_create_index", "CREATE INDEX ix_cover ON child(pid)", ["pid"]),
        new("compound_create_index", "CREATE INDEX ix_cover ON child(pid,note)", ["pid", "note"]),
        new("compound_alter_add_index", "ALTER TABLE child ADD INDEX ix_cover(pid,note)", ["pid", "note"]),
        new("compound_alter_add_key", "ALTER TABLE child ADD KEY ix_cover(pid,note)", ["pid", "note"])
    ];

    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    public static IEnumerable<object[]> CaseIds => Scenarios.Select(c => new object[] { c.Name });

    [Fact]
    public void CorpusHasFourDistinctCoveringTransitions() =>
        Assert.Equal(4, Scenarios.Select(x => x.Add).Distinct().Count());

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task EveryCoveringTransitionPreservesFkAndTracksKnownIndexOnly(string id)
    {
        var scenario = Scenarios.Single(x => x.Name == id);
        var model = SqlCoreFacade.AnalyzeScript(Graph + scenario.Add + ";",
            SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty(), Linux,
            TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await WithDatabase(async (db, database) =>
        {
            await Execute(db, model.Statements[0].Syntax.Text);
            await Execute(db, model.Statements[1].Syntax.Text);
            var before = await ReadPhysical(db, database);
            Assert.Contains(before, x => x.Name == "fk_child" && x.Column == "pid");
            Assert.DoesNotContain(before, x => x.Name == "ix_cover");
            await Execute(db, model.Statements[2].Syntax.Text);
            var after = await ReadPhysical(db, database);
            Assert.Equal(scenario.Columns,
                after.Where(x => x.Name == "ix_cover")
                    .OrderBy(x => x.Ordinal).Select(x => x.Column).ToArray());
            Assert.Equal(1, await CountForeignKey(db, database));
            var previous = model.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "child");
            Assert.Empty(previous.Indexes);
            var current = model.FinalCatalog.Tables.Single(t => t.Name == "child");
            Assert.Equal(scenario.Columns, Assert.Single(current.Indexes).Columns.ToArray());
            Assert.Equal("fk_child", current.Constraints.Single(
                c => c.Kind == SqlCatalogConstraintKind.ForeignKey).Name);

            // MySQL permits the automatic support index to disappear but
            // does not promise that it always does. This observational
            // check is conditional on the independent physical dictionary.
            if (!after.Any(x => x.Name == "fk_child"))
            {
                await Assert.ThrowsAsync<MySqlException>(async () =>
                    await Execute(db, "DROP INDEX ix_cover ON child"));
                Assert.Equal(after, await ReadPhysical(db, database));
            }
        });
    }

    [Fact]
    public async Task SameNameCoveringIndexPromotesOneKnownExplicitIndexWithoutClaimingPhysicalAbsence()
    {
        // InnoDB can promote its own implicit FK-support index under the
        // same name. Prove the explicit key and FK, not the old physical
        // implicit-index lifetime or any other index-name absence.
        var model = SqlCoreFacade.AnalyzeScript(Graph +
            "CREATE INDEX fk_child ON child(pid,note);", SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, model.Statements[2].Status);
        Assert.Equal("fk_child", Assert.Single(
            model.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes).Name);
        await WithDatabase(async (db, database) =>
        {
            await Execute(db, model.Statements[0].Syntax.Text);
            await Execute(db, model.Statements[1].Syntax.Text);
            Assert.Equal(new[] { "pid" },
                (await ReadPhysical(db, database))
                    .Where(x => x.Name == "fk_child")
                    .OrderBy(x => x.Ordinal).Select(x => x.Column).ToArray());
            await Execute(db, "CREATE INDEX fk_child ON child(pid,note)");
            Assert.Equal(new[] { "pid", "note" },
                (await ReadPhysical(db, database))
                    .Where(x => x.Name == "fk_child")
                    .OrderBy(x => x.Ordinal).Select(x => x.Column).ToArray());
            Assert.Equal(1, await CountForeignKey(db, database));
        });
    }

    private static async Task<Physical[]> ReadPhysical(MySqlConnection db, string database)
    {
        const string query = """
            SELECT INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'child'
             ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        var rows = new List<Physical>();
        await using var cmd = new MySqlCommand(query, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(new Physical(reader.GetString(0), reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2))));
        return rows.ToArray();
    }

    private static async Task<int> CountForeignKey(MySqlConnection db, string database)
    {
        const string query = """
            SELECT COUNT(*)
              FROM information_schema.REFERENTIAL_CONSTRAINTS
             WHERE CONSTRAINT_SCHEMA=@db
               AND TABLE_NAME='child' AND CONSTRAINT_NAME='fk_child'
            """;
        await using var cmd = new MySqlCommand(query, db);
        cmd.Parameters.AddWithValue("@db", database);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION required in CI.");
        Assert.Skip("Requires real MySQL and TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task WithDatabase(Func<MySqlConnection, string, Task> action)
    {
        var root = ConnectionString();
        var database = "sqlcore_fkcover_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var cmd = new MySqlCommand("SELECT @@lower_case_table_names", admin))
            Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync(
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
