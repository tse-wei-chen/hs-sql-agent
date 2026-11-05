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
/// Independent live MySQL 8 InnoDB oracle: add two FK-covering keys, then
/// delete one under proven alternative support without breaking FK DDL.
/// Per-statement information_schema.STATISTICS + REFERENTIAL_CONSTRAINTS.
/// </summary>
public sealed class SqlCoreMySqlFkAlternativeIndexLiveParityTests
{
    private sealed record Scenario(string Id, string Second, string Remove, string Remaining);
    private sealed record Physical(string Name, string Column, int Ordinal, int NonUnique);
    private static readonly Scenario[] Scenarios =
    [
        new("standalone_then_drop", "CREATE INDEX ix_backup ON child(pid)",
            "DROP INDEX ix_cover ON child", "ix_backup"),
        new("alter_key_then_drop", "ALTER TABLE child ADD KEY ix_backup(pid,note)",
            "ALTER TABLE child DROP INDEX ix_cover", "ix_backup"),
        new("same_name_then_drop", "ALTER TABLE child ADD INDEX ix_backup(pid)",
            "DROP INDEX fk_child ON child", "ix_backup")
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

    public static IEnumerable<object[]> Cases => Scenarios.Select(x => new object[] { x.Id });

    [Fact]
    public void ThreeIndependentSqlScriptsHaveDistinctIndexTransitions() =>
        Assert.Equal(3, Scenarios.Select(x => x.Second + x.Remove).Distinct().Count());

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryStepKeepsPhysicalFkAndProvenExplicitIndexDictionary(string id)
    {
        var scenario = Scenarios.Single(x => x.Id == id);
        var first = id == "same_name_then_drop" ? "fk_child" : "ix_cover";
        var sql = Graph +
            "CREATE INDEX " + first + " ON child(pid,note);" +
            scenario.Second + ";" + scenario.Remove + ";";
        var result = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await WithDatabase(async (db, database) =>
        {
            foreach (var statement in result.Statements.Take(2))
                await Execute(db, statement.Syntax.Text);
            var initial = await ReadIndexes(db, database);
            Assert.Contains(initial, x => x.Name == "PRIMARY" && x.Column == "id");
            Assert.Contains(initial, x => x.Name == "fk_child" && x.Column == "pid");

            await Execute(db, result.Statements[2].Syntax.Text);
            var firstPhysical = await ReadIndexes(db, database);
            Assert.Equal(new[] { "pid", "note" },
                firstPhysical.Where(x => x.Name == first)
                    .OrderBy(x => x.Ordinal).Select(x => x.Column).ToArray());
            await Execute(db, result.Statements[3].Syntax.Text);
            var both = await ReadIndexes(db, database);
            Assert.Contains(both, x => x.Name == first && x.Column == "pid");
            Assert.Contains(both, x => x.Name == "ix_backup" && x.Column == "pid");
            Assert.Equal(1, await CountFk(db, database));
            Assert.Equal(new[] { first, "ix_backup" }, result.Statements[3]
                .CatalogAfter.Tables.Single(t => t.Name == "child").Indexes
                .Select(x => x.Name).ToArray());

            await Execute(db, result.Statements[4].Syntax.Text);
            var final = await ReadIndexes(db, database);
            Assert.DoesNotContain(final, x =>
                x.Name == first && x.Name != scenario.Remaining);
            Assert.Contains(final, x => x.Name == scenario.Remaining && x.Column == "pid");
            Assert.Equal(1, await CountFk(db, database));
            Assert.Equal(scenario.Remaining, Assert.Single(
                result.FinalCatalog.Tables.Single(t => t.Name == "child").Indexes).Name);
            Assert.Equal("fk_child", result.FinalCatalog.Tables.Single(t => t.Name == "child")
                .Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.ForeignKey).Name);
        });
    }

    [Fact]
    public async Task WithoutAlternativeMySqlRejectsDroppingFinalFkSupportIndex()
    {
        var staticModel = SqlCoreFacade.AnalyzeScript(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "DROP INDEX ix_cover ON child;", SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, staticModel.Statements[3].Status);
        await WithDatabase(async (db, database) =>
        {
            foreach (var step in staticModel.Statements.Take(3))
                await Execute(db, step.Syntax.Text);
            var before = await ReadIndexes(db, database);
            if (!before.Any(x => x.Name == "fk_child"))
            {
                await Assert.ThrowsAsync<MySqlException>(async () =>
                    await Execute(db, "DROP INDEX ix_cover ON child"));
                Assert.Equal(before, await ReadIndexes(db, database));
            }
        });
    }

    private static async Task<Physical[]> ReadIndexes(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX, NON_UNIQUE
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'child'
             ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        var values = new List<Physical>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            values.Add(new Physical(reader.GetString(0), reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3))));
        return values.ToArray();
    }

    private static async Task<int> CountFk(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS
             WHERE CONSTRAINT_SCHEMA = @db AND TABLE_NAME = 'child'
               AND CONSTRAINT_NAME = 'fk_child'
            """;
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION required for live parity.");
        Assert.Skip("Requires real MySQL with TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task WithDatabase(Func<MySqlConnection, string, Task> action)
    {
        var root = ConnectionString();
        var database = "sqlcore_fkalt_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var cmd = new MySqlCommand("SELECT @@lower_case_table_names", admin))
            Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken)));
        await Execute(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var cs = new MySqlConnectionStringBuilder(root)
            {
                Database = database, Pooling = false
            };
            await using var db = new MySqlConnection(cs.ConnectionString);
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
