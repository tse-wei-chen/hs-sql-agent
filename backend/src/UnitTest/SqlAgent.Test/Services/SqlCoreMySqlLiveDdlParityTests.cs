using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Differential check against a disposable REAL MySQL schema. DDL is executed
/// only by this test after static analysis has completed; virtual Catalog
/// declarations are compared to independently queried information_schema.
/// </summary>
public sealed class SqlCoreMySqlLiveDdlParityTests
{
    private sealed record Column(string Name, string DataType, bool Nullable);
    private sealed record Table(string Name, Column[] Columns, string[] Indexes);

    private static readonly (string Id, string Sql)[] Scenarios =
    [
        ("inventory_add", "CREATE TABLE inventory(id INT NOT NULL, sku VARCHAR(64)); ALTER TABLE inventory ADD COLUMN qty INT;"),
        ("shipping_drop_column", "CREATE TABLE shipping(id INT, description VARCHAR(64)); ALTER TABLE shipping DROP COLUMN description;"),
        ("invoices_add_drop", "CREATE TABLE invoices(id INT, status VARCHAR(64)); ALTER TABLE invoices ADD COLUMN amount INT; ALTER TABLE invoices DROP COLUMN status;"),
        ("multi_tables", "CREATE TABLE purchases(id INT); CREATE TABLE refunds(id INT, purchase_id INT);"),
        ("drop_second", "CREATE TABLE customers(id INT); CREATE TABLE addresses(id INT); DROP TABLE addresses;"),
        ("drop_recreate", "CREATE TABLE products(id INT); DROP TABLE products; CREATE TABLE products(sku VARCHAR(64) NOT NULL);"),
        ("if_not_exists_noop", "CREATE TABLE users(id INT); CREATE TABLE IF NOT EXISTS users(other INT);"),
        ("if_exists_noop", "CREATE TABLE users(id INT); DROP TABLE users; DROP TABLE IF EXISTS users;"),
        ("index_kept", "CREATE TABLE events(id INT, state VARCHAR(64)); CREATE INDEX idx_events_state ON events(state);"),
        ("index_removed", "CREATE TABLE events(id INT, state VARCHAR(64)); CREATE INDEX idx_events_state ON events(state); DROP INDEX idx_events_state ON events;"),
        ("two_indexes", "CREATE TABLE locations(id INT, city VARCHAR(64), region VARCHAR(64)); CREATE INDEX idx_locations_city ON locations(city); CREATE INDEX idx_locations_region ON locations(region);"),
        ("index_table_drop", "CREATE TABLE jobs(id INT, status VARCHAR(64)); CREATE INDEX idx_jobs_status ON jobs(status); DROP TABLE jobs;"),
        ("quoted_simple", "CREATE TABLE `order_items`(`id` INT NOT NULL, `label` VARCHAR(64)); ALTER TABLE `order_items` ADD COLUMN `amount` INT;"),
        ("alter_then_index", "CREATE TABLE reports(id INT); ALTER TABLE reports ADD COLUMN tag VARCHAR(64); CREATE INDEX idx_reports_tag ON reports(tag);"),
        ("indexes_drop_then_column", "CREATE TABLE balances(id INT, memo VARCHAR(64)); CREATE INDEX idx_balances_memo ON balances(memo); DROP INDEX idx_balances_memo ON balances; ALTER TABLE balances DROP COLUMN memo;"),
        ("mixed_delete", "CREATE TABLE scratch(id INT); CREATE TABLE records(id INT, note VARCHAR(64)); DROP TABLE scratch; ALTER TABLE records ADD COLUMN active INT;")
    ];

    public static IEnumerable<object[]> Cases =>
        Scenarios.Select(x => new object[] { x.Id });

    [Fact]
    public void CorpusHasSixteenDistinctConcreteMySqlDdlScripts()
    {
        Assert.Equal(16, Scenarios.Length);
        Assert.Equal(16, Scenarios.Select(x => x.Id).Distinct().Count());
        Assert.Equal(16, Scenarios.Select(x => x.Sql).Distinct().Count());
        Assert.All(Scenarios, x => Assert.True(x.Sql.Count(c => c == ';') >= 2));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryModeledDdlTransitionAgreesWithRealMySql(string id)
    {
        var source = ConnectionString();
        var script = Scenarios.Single(x => x.Id == id).Sql;
        // Analyze the entire script BEFORE opening an execution connection.
        var result = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());
        Assert.True(result.Statements.Count >= 2);
        Assert.All(result.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        Assert.Empty(result.Diagnostics);

        // MySQL DDL implicitly commits. A transaction is NOT isolation here:
        // create a uniquely named database and guarantee cleanup instead.
        var database = "sqlcore_p6_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(source);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await Run(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var options = new MySqlConnectionStringBuilder(source)
            {
                Database = database,
                Pooling = false
            };
            await using var connection = new MySqlConnection(options.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            for (var index = 0; index < result.Statements.Count; index++)
            {
                var step = result.Statements[index];
                Assert.True(step.CatalogAfter.IsClosedWorld, id + "#" + index);
                await Run(connection, step.Syntax.Text);
                AssertEquivalent(id, index, step.CatalogAfter,
                    await ReadSchema(connection, database));
            }
            AssertEquivalent(id, result.Statements.Count, result.FinalCatalog,
                await ReadSchema(connection, database));
        }
        finally
        {
            await Run(admin, "DROP DATABASE IF EXISTS `" + database + "`");
        }
    }

    [Fact]
    public async Task RealMySqlDdlImplicitlyCommitsPriorInnoDbInsert()
    {
        // SQL-level START TRANSACTION and a subsequent ordinary CREATE TABLE
        // must not be modeled as a rollbackable Catalog. The MySQL server,
        // independently of SqlCore, proves the actual implicit-commit fence.
        var contract = SqlCoreFacade.AnalyzeScript(
            "START TRANSACTION; CREATE TABLE marker(id INT); ROLLBACK;",
            SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            contract.Statements[1].Status);
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            contract.Statements[1].TransactionProvenance.Origin);
        Assert.False(contract.FinalCatalog.IsClosedWorld);

        var root = ConnectionString();
        var database = "sqlcore_p6_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await Run(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var cs = new MySqlConnectionStringBuilder(root)
            {
                Database = database,
                Pooling = false
            }.ConnectionString;
            await using (var db = new MySqlConnection(cs))
            {
                await db.OpenAsync(TestContext.Current.CancellationToken);
                await Run(db, "CREATE TABLE ledger(id INT) ENGINE=InnoDB");
                await Run(db, "START TRANSACTION");
                await Run(db, "INSERT INTO ledger(id) VALUES (7)");
                await Run(db, "CREATE TABLE marker(id INT) ENGINE=InnoDB");
                await Run(db, "ROLLBACK");

                using var query = new MySqlCommand(
                    "SELECT COUNT(*) FROM ledger WHERE id = 7", db);
                Assert.Equal(1L, Convert.ToInt64(
                    await query.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
            }
        }
        finally
        {
            await Run(admin, "DROP DATABASE IF EXISTS `" + database + "`");
        }
    }

    [Fact]
    public void UnmodeledCheckIsNeverPromotedToProvenCatalogEvenWhenMySqlSupportsIt()
    {
        const string sql = "CREATE TABLE constrained(id INT, CHECK (id > 0));";
        var result = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlEffectCoverage.Unknown, result.EffectCoverage);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Real MySQL DDL parity requires TEST_MYSQL_CONNECTION in CI.");
        Assert.Skip("Live MySQL DDL parity needs TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Skipped.");
    }

    private static async Task Run(MySqlConnection db, string sql)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<Table>> ReadSchema(MySqlConnection db, string database)
    {
        var names = new List<string>();
        await using (var cmd = new MySqlCommand(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = @database AND table_type = 'BASE TABLE' ORDER BY table_name", db))
        {
            cmd.Parameters.AddWithValue("@database", database);
            await using var rd = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await rd.ReadAsync(TestContext.Current.CancellationToken))
                names.Add(rd.GetString(0));
        }
        var tables = new List<Table>();
        foreach (var name in names)
        {
            var columns = new List<Column>();
            await using (var cmd = new MySqlCommand(
                "SELECT column_name, column_type, is_nullable FROM information_schema.columns " +
                "WHERE table_schema = @database AND table_name = @table ORDER BY ordinal_position", db))
            {
                cmd.Parameters.AddWithValue("@database", database);
                cmd.Parameters.AddWithValue("@table", name);
                await using var rd = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await rd.ReadAsync(TestContext.Current.CancellationToken))
                    columns.Add(new Column(rd.GetString(0), rd.GetString(1), rd.GetString(2) == "YES"));
            }
            var indexes = new List<string>();
            await using (var cmd = new MySqlCommand(
                "SELECT DISTINCT index_name FROM information_schema.statistics " +
                "WHERE table_schema = @database AND table_name = @table " +
                "AND index_name <> 'PRIMARY' ORDER BY index_name", db))
            {
                cmd.Parameters.AddWithValue("@database", database);
                cmd.Parameters.AddWithValue("@table", name);
                await using var rd = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await rd.ReadAsync(TestContext.Current.CancellationToken))
                    indexes.Add(rd.GetString(0));
            }
            tables.Add(new Table(name, columns.ToArray(), indexes.ToArray()));
        }
        return tables;
    }

    private static void AssertEquivalent(string scenario, int step, SqlCatalogState modeled, IReadOnlyList<Table> actual)
    {
        Assert.True(modeled.IsClosedWorld, $"{scenario}#{step}: the Catalog is not proven");
        Assert.Equal(modeled.Tables.Count, actual.Count);
        foreach (var table in modeled.Tables)
        {
            Assert.Equal(SqlCatalogKnowledge.Known, table.Knowledge);
            var live = Assert.Single(actual.Where(x =>
                string.Equals(x.Name, table.Name, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(table.Columns.Count, live.Columns.Length);
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var expected = table.Columns[c];
                var observed = live.Columns[c];
                Assert.True(string.Equals(expected.Name, observed.Name, StringComparison.OrdinalIgnoreCase),
                    $"{scenario}#{step}: column mismatch: {expected.Name} vs {observed.Name}");
                Assert.True(string.Equals(expected.TypeName, observed.DataType, StringComparison.OrdinalIgnoreCase),
                    $"{scenario}#{step}: type mismatch: {expected.TypeName} vs {observed.DataType}");
                Assert.Equal(expected.Nullable, observed.Nullable);
            }
            Assert.Equal(
                table.Indexes.Select(x => x.Name).Order(StringComparer.OrdinalIgnoreCase),
                live.Indexes.Order(StringComparer.OrdinalIgnoreCase));
        }
    }
}
