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
/// Isolated real MySQL 8 InnoDB DDL differential oracle. After EVERY
/// statement compare exact FK owner, symbol, parent and ordered physical
/// local/remote columns against information_schema.KEY_COLUMN_USAGE.
/// DDL commits implicitly: isolated database per case + explicit cleanup.
/// </summary>
public sealed class SqlCoreMySqlForeignKeyLiveParityTests
{
    private sealed record Fk(string Owner, string Symbol, string Parent,
        string[] Local, string[] Remote);
    private sealed record Case(string Name, string Script);
    private static readonly Case[] Scenarios =
    [
        new("single_child_then_drop_parent",
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(pid INT, note INT, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));" +
            "ALTER TABLE child DROP FOREIGN KEY fk_child;" +
            "DROP TABLE parent;"),
        new("two_fk_single_child_atomic_drop_and_columns",
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL UNIQUE);" +
            "CREATE TABLE child(pid INT, code INT, note INT, " +
            "CONSTRAINT fk_id FOREIGN KEY(pid) REFERENCES parent(id)," +
            "CONSTRAINT fk_code FOREIGN KEY(code) REFERENCES parent(code));" +
            "ALTER TABLE child DROP FOREIGN KEY fk_id, DROP FOREIGN KEY fk_code, DROP COLUMN pid, DROP COLUMN code;"),
        new("composite_fk_drop_and_columns",
            "CREATE TABLE parent(a INT NOT NULL, b INT NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(a,b));" +
            "CREATE TABLE child(x INT, y INT, note INT, " +
            "CONSTRAINT fk_pair FOREIGN KEY(x,y) REFERENCES parent(a,b));" +
            "ALTER TABLE child DROP FOREIGN KEY fk_pair, DROP COLUMN x, DROP COLUMN y;"),
        new("self_referencing_fk_and_local_column",
            "CREATE TABLE tree(id INT NOT NULL PRIMARY KEY, parent_id INT, " +
            "CONSTRAINT fk_tree FOREIGN KEY(parent_id) REFERENCES tree(id));" +
            "ALTER TABLE tree DROP FOREIGN KEY fk_tree, DROP parent_id;"),
        new("other_child_fk_stays",
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE first_child(pid INT, extra INT, CONSTRAINT fk_first FOREIGN KEY(pid) REFERENCES parent(id));" +
            "CREATE TABLE second_child(pid INT, CONSTRAINT fk_second FOREIGN KEY(pid) REFERENCES parent(id));" +
            "ALTER TABLE first_child DROP FOREIGN KEY fk_first, DROP COLUMN pid;" +
            "ALTER TABLE second_child DROP FOREIGN KEY fk_second;" +
            "DROP TABLE parent;"),
        new("batch_two_fks_preserves_other_key",
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL UNIQUE);" +
            "CREATE TABLE child(pid INT, code INT, label INT, " +
            "CONSTRAINT fk_id FOREIGN KEY(pid) REFERENCES parent(id)," +
            "CONSTRAINT fk_code FOREIGN KEY(code) REFERENCES parent(code));" +
            "ALTER TABLE child DROP FOREIGN KEY fk_id, DROP FOREIGN KEY fk_code;" +
            "ALTER TABLE child DROP COLUMN code;")
    ];

    public static IEnumerable<object[]> Cases => Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void CorpusHasSixIndependentMultiStepInnoDbWorkflows()
    {
        Assert.Equal(6, Scenarios.Length);
        Assert.Equal(6, Scenarios.Select(s => s.Script).Distinct().Count());
        Assert.All(Scenarios, s => Assert.True(s.Script.Count(c => c == ';') >= 2));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryStatementMatchesRealMySqlForeignKeyDictionary(string id)
    {
        var script = Scenarios.Single(s => s.Name == id).Script;
        var premise = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL, null, default, null,
            new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LOWER_CASE_TABLE_NAMES"] = "0"
            });
        // Complete static analysis BEFORE connecting to the execution DB.
        var model = SqlCoreFacade.AnalyzeScript(script, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), premise, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));

        var root = ConnectionString();
        var database = "sqlcore_fk_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        using (var variable = new MySqlCommand("SELECT @@lower_case_table_names", admin))
            Assert.Equal(0, Convert.ToInt32(await variable.ExecuteScalarAsync(
                TestContext.Current.CancellationToken)));
        await Execute(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var builder = new MySqlConnectionStringBuilder(root)
            {
                Database = database, Pooling = false
            };
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            for (var i = 0; i < model.Statements.Count; i++)
            {
                var step = model.Statements[i];
                Assert.True(step.CatalogAfter.IsClosedWorld, id + "#" + i);
                await Execute(connection, step.Syntax.Text);
                var actual = await ReadForeignKeys(connection, database);
                var expected = step.CatalogAfter.Tables.SelectMany(t =>
                        t.Constraints
                        .Where(c => c.Kind == SqlCatalogConstraintKind.ForeignKey)
                        .Select(c => new Fk(t.QualifiedName!.Parts[^1], c.Name,
                            c.ReferencedIdentity!.Parts[^1],
                            c.Columns.ToArray(), c.ReferencedColumns.ToArray())))
                    .OrderBy(x => x.Owner, StringComparer.Ordinal)
                    .ThenBy(x => x.Symbol, StringComparer.Ordinal).ToArray();
                Assert.Equal(expected.Length, actual.Length);
                for (var k = 0; k < expected.Length; k++)
                {
                    Assert.Equal(expected[k].Owner, actual[k].Owner);
                    Assert.Equal(expected[k].Symbol, actual[k].Symbol);
                    Assert.Equal(expected[k].Parent, actual[k].Parent);
                    Assert.Equal(expected[k].Local, actual[k].Local);
                    Assert.Equal(expected[k].Remote, actual[k].Remote);
                }
                var actualCols = await ReadColumns(connection, database);
                var expectedCols = step.CatalogAfter.Tables
                    .SelectMany(t => t.Columns.Select(c => (Owner:t.QualifiedName!.Parts[^1], Name:c.Name)))
                    .OrderBy(x => x.Owner, StringComparer.Ordinal)
                    .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
                Assert.Equal(expectedCols, actualCols);
            }
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + database + "`");
        }
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION required for live MySQL FK parity in CI.");
        Assert.Skip("Requires real MySQL and TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task Execute(MySqlConnection db, string sql)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Fk[]> ReadForeignKeys(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT TABLE_NAME, CONSTRAINT_NAME, REFERENCED_TABLE_NAME,
                   COLUMN_NAME, REFERENCED_COLUMN_NAME, ORDINAL_POSITION
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @db AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION
            """;
        var rows = new List<(string Owner, string Symbol, string Parent,
            string Local, string Remote, int Ordinal)>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var rd = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rd.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2),
                rd.GetString(3), rd.GetString(4), rd.GetInt32(5)));
        return rows.GroupBy(x => (x.Owner, x.Symbol, x.Parent))
            .Select(g => new Fk(g.Key.Owner, g.Key.Symbol, g.Key.Parent,
                g.OrderBy(x => x.Ordinal).Select(x => x.Local).ToArray(),
                g.OrderBy(x => x.Ordinal).Select(x => x.Remote).ToArray()))
            .OrderBy(x => x.Owner, StringComparer.Ordinal)
            .ThenBy(x => x.Symbol, StringComparer.Ordinal).ToArray();
    }

    private static async Task<(string Owner, string Name)[]> ReadColumns(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA=@db
            ORDER BY TABLE_NAME, COLUMN_NAME
            """;
        var cols = new List<(string Owner, string Name)>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var rd = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rd.ReadAsync(TestContext.Current.CancellationToken))
            cols.Add((rd.GetString(0), rd.GetString(1)));
        return cols.ToArray();
    }
}
