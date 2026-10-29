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
/// Independent MySQL 8 InnoDB physical index oracle. Every statement checks
/// exact PRIMARY, UNIQUE and standalone index names, member order and flags.
/// No SqlCore production code opens a connection or executes this DDL.
/// </summary>
public sealed class SqlCoreMySqlKeyIndexNamespaceLiveParityTests
{
    private sealed record Scenario(string Id, string Script);
    private sealed record IndexPart(string Owner, string Name, string Column, int Ordinal,
        int NonUnique);

    private static readonly Scenario[] Scenarios =
    [
        new("primary_and_secondary",
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, note INT);" +
            "CREATE INDEX ix_note ON orders(note);" +
            "ALTER TABLE orders DROP INDEX ix_note;"),
        new("named_unique_and_primary",
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, note INT, " +
            "CONSTRAINT uq_code UNIQUE(code));" +
            "CREATE INDEX ix_note ON orders(note);" +
            "ALTER TABLE orders DROP KEY ix_note;"),
        new("composite_keys_and_alter_index",
            "CREATE TABLE orders(id INT NOT NULL, region INT NOT NULL, code INT, note INT, " +
            "CONSTRAINT pk_orders PRIMARY KEY(id,region), " +
            "CONSTRAINT uq_code UNIQUE(code));" +
            "ALTER TABLE orders ADD KEY ix_note(note,id);" +
            "ALTER TABLE orders DROP INDEX ix_note;"),
        new("per_table_namespaces",
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, note INT);" +
            "CREATE TABLE events(id INT NOT NULL PRIMARY KEY, note INT);" +
            "CREATE INDEX ix_note ON orders(note);" +
            "ALTER TABLE events ADD INDEX ix_note(note);")
    ];

    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    [Fact]
    public void CorpusHasFourIndependentPhysicalIndexWorkflows() =>
        Assert.Equal(4, Scenarios.Select(x => x.Script).Distinct().Count());

    public static IEnumerable<object[]> Cases => Scenarios.Select(x => new object[] { x.Id });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryStatementMatchesRealMySqlIndexDictionary(string id)
    {
        var scenario = Scenarios.Single(x => x.Id == id);
        var model = Analyze(scenario.Script);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        var root = ConnectionString();
        var dbName = "sqlcore_keyns_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await AssertServerMode(admin);
        await Execute(admin, "CREATE DATABASE `" + dbName + "`");
        try
        {
            var connectionString = new MySqlConnectionStringBuilder(root)
            {
                Database = dbName, Pooling = false
            }.ConnectionString;
            await using var db = new MySqlConnection(connectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            foreach (var step in model.Statements)
            {
                Assert.True(step.CatalogAfter.IsClosedWorld);
                await Execute(db, step.Syntax.Text);
                var expected = step.CatalogAfter.Tables
                    .SelectMany(t =>
                        t.Constraints
                            .Where(c => c.Kind != SqlCatalogConstraintKind.ForeignKey)
                            .SelectMany(c => c.Columns.Select((column, n) => new IndexPart(
                                t.QualifiedName!.Parts[^1],
                                c.Kind == SqlCatalogConstraintKind.PrimaryKey ? "PRIMARY" : c.Name,
                                column, n + 1, 0)))
                        .Concat(t.Indexes.SelectMany(index =>
                            index.Columns.Select((column, n) => new IndexPart(
                                t.QualifiedName!.Parts[^1], index.Name, column, n + 1, 1)))))
                    .OrderBy(x => x.Owner, StringComparer.Ordinal)
                    .ThenBy(x => x.Name, StringComparer.Ordinal)
                    .ThenBy(x => x.Ordinal).ToArray();
                Assert.Equal(expected, await ReadPhysicalIndexes(db, dbName));
            }
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + dbName + "`");
        }
    }

    [Theory]
    [InlineData("CREATE INDEX `PRIMARY` ON orders(note)")]
    [InlineData("CREATE INDEX `uq_code` ON orders(note)")]
    public async Task RealMySqlRejectsExplicitKeyBackedIndexNameCollision(string attempted)
    {
        const string create =
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, code INT, note INT, " +
            "CONSTRAINT uq_code UNIQUE(code))";
        var staticResult = Analyze(create + ";" + attempted + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            staticResult.Statements[1].Status);
        Assert.DoesNotContain(staticResult.Statements[1].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);

        var root = ConnectionString();
        var dbName = "sqlcore_keyns_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await AssertServerMode(admin);
        await Execute(admin, "CREATE DATABASE `" + dbName + "`");
        try
        {
            var options = new MySqlConnectionStringBuilder(root)
            {
                Database = dbName, Pooling = false
            };
            await using var db = new MySqlConnection(options.ConnectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await Execute(db, create);
            var before = await ReadPhysicalIndexes(db, dbName);
            await Assert.ThrowsAsync<MySqlException>(async () => await Execute(db, attempted));
            Assert.Equal(before, await ReadPhysicalIndexes(db, dbName));
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + dbName + "`");
        }
    }

    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux,
            TestContext.Current.CancellationToken);

    private static async Task<IndexPart[]> ReadPhysicalIndexes(MySqlConnection db, string dbName)
    {
        const string sql = """
            SELECT TABLE_NAME, INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX, NON_UNIQUE
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @schema
            ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX
            """;
        var rows = new List<IndexPart>();
        await using var command = new MySqlCommand(sql, db);
        command.Parameters.AddWithValue("@schema", dbName);
        await using var reader =
            await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(new IndexPart(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), Convert.ToInt32(reader.GetValue(3)),
                Convert.ToInt32(reader.GetValue(4))));
        return rows.OrderBy(x => x.Owner, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Ordinal).ToArray();
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION required for physical MySQL parity.");
        Assert.Skip("Requires real MySQL and TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task AssertServerMode(MySqlConnection db)
    {
        await using var cmd = new MySqlCommand("SELECT @@lower_case_table_names", db);
        Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken)));
    }

    private static async Task Execute(MySqlConnection db, string sql)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
