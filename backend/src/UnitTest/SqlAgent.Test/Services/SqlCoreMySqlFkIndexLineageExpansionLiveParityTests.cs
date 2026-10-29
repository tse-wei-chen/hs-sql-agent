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
/// Real MySQL InnoDB differential for auto-created FK supporting index
/// same-name promotion and multiple independent named child-side FK indexes.
/// Only physical evidence is inspected: SqlCore does not execute database SQL.
/// </summary>
public sealed class SqlCoreMySqlFkIndexLineageExpansionLiveParityTests
{
    private sealed record Scenario(string Name, bool Multiple, string Add, string ExplicitName,
        string[] ExpectedColumns, bool Replacement);
    private sealed record IndexPart(string Name, string Column, int Ordinal, int NonUnique);
    private const string OneFk =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, note INT, " +
        "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));";
    private const string TwoFk =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p1 INT, p2 INT, note INT, " +
        "CONSTRAINT fk_one FOREIGN KEY(p1) REFERENCES parent(id), " +
        "CONSTRAINT fk_two FOREIGN KEY(p2) REFERENCES parent(id));";

    private static readonly Scenario[] Scenarios =
    [
        new("single_same_name_create_index", false,
            "CREATE INDEX fk_child ON child(pid,note)", "fk_child", ["pid", "note"], true),
        new("single_same_name_alter_key", false,
            "ALTER TABLE child ADD KEY fk_child(pid,note)", "fk_child", ["pid", "note"], true),
        new("multiple_disjoint_new_index", true,
            "CREATE INDEX ix_note ON child(note)", "ix_note", ["note"], false),
        new("multiple_same_name_promote_one", true,
            "ALTER TABLE child ADD INDEX fk_one(p1,note)", "fk_one", ["p1", "note"], true)
    ];

    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    public static IEnumerable<object[]> Cases => Scenarios.Select(x => new object[] { x.Name });

    [Fact]
    public void CorpusContainsFourIndependentMySqlPhysicalIndexTransitions() =>
        Assert.Equal(4, Scenarios.Select(x => x.Add).Distinct().Count());

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RealMySqlIndexAndForeignKeyDictionariesMatchProvenStaticTransition(string name)
    {
        var scenario = Scenarios.Single(x => x.Name == name);
        var script = (scenario.Multiple ? TwoFk : OneFk) + scenario.Add + ";";
        var model = SqlCoreFacade.AnalyzeScript(script, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await WithDatabase(async (db, database) =>
        {
            foreach (var step in model.Statements.Take(2))
                await Execute(db, step.Syntax.Text);
            var before = await ReadIndexes(db, database);
            var beforeFk = await ReadFkNames(db, database);
            Assert.Equal(scenario.Multiple ? new[] { "fk_one", "fk_two" } :
                new[] { "fk_child" }, beforeFk);

            await Execute(db, model.Statements[2].Syntax.Text);
            var after = await ReadIndexes(db, database);
            Assert.Equal(scenario.ExpectedColumns,
                after.Where(x => x.Name == scenario.ExplicitName)
                    .OrderBy(x => x.Ordinal).Select(x => x.Column).ToArray());
            Assert.Equal(beforeFk, await ReadFkNames(db, database));
            Assert.Contains(after, x => x.Name == "PRIMARY" && x.Column == "id");
            if (!scenario.Replacement)
            {
                Assert.Equal(before, after.Where(x => x.Name != scenario.ExplicitName).ToArray());
            }
            else
            {
                // Same-name index promotion changes the ordinal index members
                // but does not drop its foreign-key constraint.
                Assert.Single(after, x => x.Name == scenario.ExplicitName &&
                    x.Column == scenario.ExpectedColumns[0]);
            }
            var table = model.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child");
            Assert.Equal(scenario.ExplicitName, Assert.Single(table.Indexes).Name);
            Assert.Equal(beforeFk, table.Constraints
                .Where(x => x.Kind == SqlCatalogConstraintKind.ForeignKey)
                .Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());

            // Physical engine can reject DROP of last FK supporting index.
            // This is not treated as proof that all auto indexes were removed.
            if (scenario.Replacement &&
                !after.Any(x => x.Name == (scenario.Multiple ? "fk_one" : "fk_child") &&
                    x.Ordinal == 1 && x.Column == (scenario.Multiple ? "p1" : "pid")
                    && x.Name != scenario.ExplicitName))
            {
                await Assert.ThrowsAsync<MySqlException>(async () =>
                    await Execute(db, "DROP INDEX `" + scenario.ExplicitName + "` ON child"));
                Assert.Equal(after, await ReadIndexes(db, database));
            }
        });
    }

    [Theory]
    [InlineData("CREATE INDEX fk_child ON child(note)")]
    [InlineData("ALTER TABLE child ADD KEY fk_child(note)")]
    public async Task SameNameButWrongColumnPrefixCannotOverwriteFkSupport(string sql)
    {
        var model = SqlCoreFacade.AnalyzeScript(OneFk + sql + ";",
            SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty(), Linux,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, model.Statements[2].Status);
        await WithDatabase(async (db, database) =>
        {
            foreach (var step in model.Statements.Take(2))
                await Execute(db, step.Syntax.Text);
            var old = await ReadIndexes(db, database);
            await Assert.ThrowsAsync<MySqlException>(async () => await Execute(db, sql));
            Assert.Equal(old, await ReadIndexes(db, database));
        });
    }

    private static async Task<IndexPart[]> ReadIndexes(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX, NON_UNIQUE
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'child'
             ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        var rows = new List<IndexPart>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(new IndexPart(reader.GetString(0), reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3))));
        return rows.ToArray();
    }

    private static async Task<string[]> ReadFkNames(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS
             WHERE CONSTRAINT_SCHEMA = @db AND TABLE_NAME = 'child'
             ORDER BY CONSTRAINT_NAME
            """;
        var rows = new List<string>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION required for live FK lineage parity.");
        Assert.Skip("Requires real MySQL with TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task WithDatabase(Func<MySqlConnection, string, Task> action)
    {
        var root = ConnectionString();
        var database = "sqlcore_fklineage_" + Guid.NewGuid().ToString("N");
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
