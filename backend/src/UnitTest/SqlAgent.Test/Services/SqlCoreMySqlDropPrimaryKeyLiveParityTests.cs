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
/// Real MySQL 8 InnoDB differential: PRIMARY constraint identity, ordered
/// key columns, and physical PRIMARY index after EACH independently modeled
/// statement. DDL implicitly commits; use isolated databases and cleanup.
/// </summary>
public sealed class SqlCoreMySqlDropPrimaryKeyLiveParityTests
{
    private sealed record Case(string Name, string Script);
    private static readonly Case[] Scenarios =
    [
        new("single_key_then_drop_column",
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, note INT);" +
            "ALTER TABLE parent DROP PRIMARY KEY;" +
            "ALTER TABLE parent DROP COLUMN id;"),
        new("named_composite_key_then_drop_column",
            "CREATE TABLE parent(a INT NOT NULL, b INT NOT NULL, note INT, " +
            "CONSTRAINT pk_parent PRIMARY KEY(a,b));" +
            "ALTER TABLE parent DROP PRIMARY KEY;" +
            "ALTER TABLE parent DROP COLUMN a;"),
        new("unique_key_survives_primary_drop",
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL UNIQUE, note INT);" +
            "ALTER TABLE parent DROP PRIMARY KEY;" +
            "ALTER TABLE parent DROP COLUMN id;"),
        new("outgoing_fk_survives_child_pk_drop",
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, " +
            "CONSTRAINT fk_parent FOREIGN KEY(pid) REFERENCES parent(id));" +
            "ALTER TABLE child DROP PRIMARY KEY;")
    ];

    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    [Fact]
    public void DifferentialCorpusContainsFourIndependentMultiStepScripts() =>
        Assert.Equal(4, Scenarios.Select(c => c.Script).Distinct().Count());

    public static IEnumerable<object[]> Cases => Scenarios.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PhysicalPrimaryKeysMatchCatalogAfterEveryStatement(string name)
    {
        var script = Scenarios.Single(c => c.Name == name).Script;
        // Static parse/analysis is completed before opening a connection.
        var model = Analyze(script);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements,
            s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));

        var root = ConnectionString();
        var database = "sqlcore_pk_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await AssertServerPremise(admin);
        await Execute(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var builder = new MySqlConnectionStringBuilder(root)
            {
                Database = database, Pooling = false
            };
            await using var db = new MySqlConnection(builder.ConnectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            for (var i = 0; i < model.Statements.Count; i++)
            {
                var step = model.Statements[i];
                Assert.True(step.CatalogAfter.IsClosedWorld, name + "#" + i);
                await Execute(db, step.Syntax.Text);
                var actualPk = await ReadOrderedPrimaryMetadata(db, database);
                var expectedPk = step.CatalogAfter.Tables
                    .SelectMany(table => table.Constraints
                        .Where(c => c.Kind == SqlCatalogConstraintKind.PrimaryKey)
                        .SelectMany(c => c.Columns.Select((col, ordinal) =>
                            (Owner: table.QualifiedName!.Parts[^1], Column: col, Ordinal: ordinal + 1))))
                    .OrderBy(c => c.Owner, StringComparer.Ordinal)
                    .ThenBy(c => c.Ordinal).ToArray();
                Assert.Equal(expectedPk, actualPk.Constraints);
                // A MySQL PRIMARY KEY is also a physical index named PRIMARY.
                Assert.Equal(expectedPk, actualPk.Indexes);
                var actualColumns = await ReadColumns(db, database);
                var expectedColumns = step.CatalogAfter.Tables
                    .SelectMany(t => t.Columns.Select(c =>
                        (Owner: t.QualifiedName!.Parts[^1], Column: c.Name)))
                    .OrderBy(c => c.Owner, StringComparer.Ordinal)
                    .ThenBy(c => c.Column, StringComparer.Ordinal).ToArray();
                Assert.Equal(expectedColumns, actualColumns);
            }
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + database + "`");
        }
    }

    [Fact]
    public async Task IncomingForeignKeyBlocksPhysicalPrimaryDropAndStaticProof()
    {
        const string setup =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(pid INT, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));";
        var staticResult = Analyze(setup + "ALTER TABLE parent DROP PRIMARY KEY;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, staticResult.Statements[^1].Status);
        Assert.DoesNotContain(staticResult.Statements[^1].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);

        var root = ConnectionString();
        var database = "sqlcore_pk_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await AssertServerPremise(admin);
        await Execute(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var builder = new MySqlConnectionStringBuilder(root)
            {
                Database = database, Pooling = false
            };
            await using var db = new MySqlConnection(builder.ConnectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            foreach (var step in staticResult.Statements.Take(2))
                await Execute(db, step.Syntax.Text);
            await Assert.ThrowsAsync<MySqlException>(async () =>
                await Execute(db, "ALTER TABLE parent DROP PRIMARY KEY"));
            var physical = await ReadOrderedPrimaryMetadata(db, database);
            Assert.Equal(new[] { (Owner: "parent", Column: "id", Ordinal: 1) },
                physical.Constraints);
            Assert.Equal(physical.Constraints, physical.Indexes);
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + database + "`");
        }
    }

    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux,
            TestContext.Current.CancellationToken);

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION required for live MySQL PRIMARY parity.");
        Assert.Skip("Requires real MySQL and TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task AssertServerPremise(MySqlConnection connection)
    {
        await using var cmd = new MySqlCommand("SELECT @@lower_case_table_names", connection);
        Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken)));
    }

    private static async Task Execute(MySqlConnection db, string sql)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(
        (string Owner, string Column, int Ordinal)[] Constraints,
        (string Owner, string Column, int Ordinal)[] Indexes)>
        ReadOrderedPrimaryMetadata(MySqlConnection db, string database)
    {
        const string keySql = """
            SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @db AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """;
        const string indexSql = """
            SELECT TABLE_NAME, COLUMN_NAME, SEQ_IN_INDEX
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db AND INDEX_NAME = 'PRIMARY'
            ORDER BY TABLE_NAME, SEQ_IN_INDEX
            """;
        async Task<(string Owner, string Column, int Ordinal)[]> Read(string sql)
        {
            var data = new List<(string Owner, string Column, int Ordinal)>();
            await using var cmd = new MySqlCommand(sql, db);
            cmd.Parameters.AddWithValue("@db", database);
            await using var reader = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                data.Add((reader.GetString(0), reader.GetString(1),
                    Convert.ToInt32(reader.GetValue(2))));
            return data.ToArray();
        }
        return (await Read(keySql), await Read(indexSql));
    }

    private static async Task<(string Owner, string Column)[]> ReadColumns(
        MySqlConnection db, string database)
    {
        const string sql = """
            SELECT TABLE_NAME, COLUMN_NAME
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @db
            ORDER BY TABLE_NAME, COLUMN_NAME
            """;
        var rows = new List<(string Owner, string Column)>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows.ToArray();
    }
}
