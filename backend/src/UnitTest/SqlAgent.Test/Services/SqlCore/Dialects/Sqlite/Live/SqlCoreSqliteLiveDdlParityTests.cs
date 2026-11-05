using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Live SQLite DDL differential contract: model every proven virtual Catalog
/// state against an independently executed SQLite in-memory schema.
/// Parse and Analyze never touch the connection; the harness alone executes SQL.
/// </summary>
public sealed class SqlCoreSqliteLiveDdlParityTests
{
    private sealed record SchemaColumn(string Name, string Type, bool Nullable);
    private sealed record SchemaTable(string Name, SchemaColumn[] Columns, string[] Indexes);

    private static readonly (string Name, string Sql)[] Scenarios =
    [
        ("create_add", "CREATE TABLE inventory (id INTEGER NOT NULL, sku TEXT); ALTER TABLE inventory ADD COLUMN qty INTEGER;"),
        ("create_add_drop_column", "CREATE TABLE orders (id INTEGER NOT NULL, note TEXT); ALTER TABLE orders ADD COLUMN status TEXT; ALTER TABLE orders DROP COLUMN note;"),
        ("two_tables", "CREATE TABLE invoices (id INTEGER, amount INTEGER); CREATE TABLE payments (id INTEGER, invoice_id INTEGER);"),
        ("two_tables_drop_one", "CREATE TABLE customers (id INTEGER); CREATE TABLE addresses (id INTEGER, customer_id INTEGER); DROP TABLE addresses;"),
        ("table_drop_recreate", "CREATE TABLE products (id INTEGER); DROP TABLE products; CREATE TABLE products (code TEXT NOT NULL);"),
        ("conditional_create_noop", "CREATE TABLE users (id INTEGER); CREATE TABLE IF NOT EXISTS users (other TEXT);"),
        ("conditional_drop_noop", "CREATE TABLE users (id INTEGER); DROP TABLE IF EXISTS users; DROP TABLE IF EXISTS users;"),
        ("index_create_drop", "CREATE TABLE events (id INTEGER NOT NULL, state TEXT); CREATE INDEX idx_events_state ON events(state); DROP INDEX idx_events_state;"),
        ("index_kept", "CREATE TABLE shipments (id INTEGER NOT NULL, region TEXT); CREATE INDEX idx_shipments_region ON shipments(region);"),
        ("index_table_drop", "CREATE TABLE jobs (id INTEGER, region TEXT); CREATE INDEX idx_jobs_region ON jobs(region); DROP TABLE jobs;"),
        ("two_indexes", "CREATE TABLE listings (id INTEGER, seller TEXT, status TEXT); CREATE INDEX idx_listings_seller ON listings(seller); CREATE INDEX idx_listings_status ON listings(status);"),
        ("quoted_simple_identifier", "CREATE TABLE \"order_items\" (\"id\" INTEGER NOT NULL, \"label\" TEXT); ALTER TABLE \"order_items\" ADD COLUMN \"amount\" INTEGER;"),
        ("quoted_dotted_identifier", "CREATE TABLE \"log.entries\" (\"id\" INTEGER); CREATE TABLE log (id INTEGER);"),
        ("transaction_rollback", "BEGIN; CREATE TABLE transient (id INTEGER); ROLLBACK;"),
        ("transaction_commit", "BEGIN; CREATE TABLE persistent (id INTEGER); COMMIT;"),
        ("savepoint_rollback", "SAVEPOINT root; CREATE TABLE staged (id INTEGER); ROLLBACK TO root; RELEASE root;"),
        ("transaction_rollback_index", "CREATE TABLE ledger (id INTEGER); BEGIN; CREATE INDEX idx_ledger_id ON ledger(id); ROLLBACK;"),
        ("transaction_savepoint", "BEGIN; CREATE TABLE stable (id INTEGER); SAVEPOINT mark; CREATE TABLE discarded (id INTEGER); ROLLBACK TO mark; RELEASE mark; COMMIT;"),
        ("add_then_create_index", "CREATE TABLE readings (id INTEGER, value INTEGER); ALTER TABLE readings ADD COLUMN unit TEXT; CREATE INDEX idx_readings_unit ON readings(unit);"),
        ("mixed_create_alter_drop", "CREATE TABLE sessions (id INTEGER); CREATE TABLE tokens (id INTEGER, session_id INTEGER); ALTER TABLE sessions ADD COLUMN label TEXT; DROP TABLE tokens;")
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProvenCatalogTransitionsMatchLiveSQLiteAfterEveryStatement(string scenario)
    {
        var sql = Scenarios.Single(x => x.Name == scenario).Sql;
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        var serverVersion = Version.Parse(Scalar(db, "SELECT sqlite_version()")!);
        Assert.True(serverVersion >= new Version(3, 35),
            $"SQLite DROP COLUMN needs 3.35+, but runtime is {serverVersion}");

        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, serverVersion);
        // Deliberately analyze completely BEFORE executing any statement.
        var report = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty(), profile);
        Assert.True(report.Statements.Count >= 2);
        Assert.All(report.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Empty(report.Diagnostics);

        for (var i = 0; i < report.Statements.Count; i++)
        {
            var statement = report.Statements[i];
            Assert.True(statement.CatalogAfter.IsClosedWorld,
                $"{scenario}#{i}: analysis lost previously proven Catalog");
            using var command = db.CreateCommand();
            command.CommandText = statement.Syntax.Text;
            command.ExecuteNonQuery();

            AssertSchemaEquals(scenario, i, statement.CatalogAfter, ReadSchema(db));
        }

        AssertSchemaEquals(scenario, report.Statements.Count,
            report.FinalCatalog, ReadSchema(db));
    }

    public static IEnumerable<object[]> Cases =>
        Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void MatrixHasTwentyIndependentMultiStatementSqliteWorkflows()
    {
        Assert.Equal(20, Scenarios.Length);
        Assert.Equal(20, Scenarios.Select(s => s.Name).Distinct().Count());
        Assert.Equal(20, Scenarios.Select(s => s.Sql).Distinct().Count());
        Assert.All(Scenarios, example =>
            Assert.True(example.Sql.Count(c => c == ';') >= 2, example.Name));
    }

    [Fact]
    public void RealSqliteAcceptsChecksThatAnalyzerStillFailsClosed()
    {
        const string sql = "CREATE TABLE unmodeled (id INTEGER, CHECK(id > 0));";
        var analysis = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            Assert.Single(analysis.Statements).Status);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlEffectCoverage.Unknown, analysis.EffectCoverage);

        using var sqlite = new SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        using var command = sqlite.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        Assert.Single(ReadSchema(sqlite)); // Unsupported by analyzer != invalid in SQLite.
    }

    private static void AssertSchemaEquals(string caseName, int index,
        SqlCatalogState model, IReadOnlyList<SchemaTable> live)
    {
        var expected = model.Tables.OrderBy(t => t.QualifiedName.Key, StringComparer.Ordinal).ToArray();
        var actual = live.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected.Length, actual.Length);

        foreach (var table in expected)
        {
            Assert.Equal(SqlCatalogKnowledge.Known, table.Knowledge);
            var physical = Assert.Single(actual.Where(
                x => string.Equals(x.Name, table.Name, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(table.Columns.Count, physical.Columns.Length);
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var expectedCol = table.Columns[c];
                var actualCol = physical.Columns[c];
                Assert.True(string.Equals(expectedCol.Name, actualCol.Name,
                    StringComparison.OrdinalIgnoreCase),
                    $"{caseName}#{index}/{table.Name}[{c}] expected {expectedCol.Name}, actual {actualCol.Name}");
                Assert.True(string.Equals(expectedCol.TypeName, actualCol.Type,
                    StringComparison.OrdinalIgnoreCase),
                    $"{caseName}#{index}/{table.Name}.{expectedCol.Name}: type {expectedCol.TypeName} != {actualCol.Type}");
                Assert.Equal(expectedCol.Nullable, actualCol.Nullable);
            }
            var expectedIndexes = table.Indexes.Select(x => x.Name)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var actualIndexes = physical.Indexes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.Equal(expectedIndexes.Length, actualIndexes.Length);
            for (var i = 0; i < expectedIndexes.Length; i++)
                Assert.True(string.Equals(expectedIndexes[i], actualIndexes[i],
                    StringComparison.OrdinalIgnoreCase),
                    $"{caseName}#{index}: index {expectedIndexes[i]} != {actualIndexes[i]}");
        }
    }

    private static IReadOnlyList<SchemaTable> ReadSchema(SqliteConnection db)
    {
        var tables = new List<SchemaTable>();
        using var list = db.CreateCommand();
        list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' " +
                           "AND name NOT LIKE 'sqlite_%' ORDER BY name COLLATE NOCASE";
        using var reader = list.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        reader.Close();

        foreach (var tableName in names)
        {
            static string Quote(string name) => "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
            var columns = new List<SchemaColumn>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + Quote(tableName) + ")";
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                    columns.Add(new SchemaColumn(
                        rows.GetString(1), rows.GetString(2),
                        rows.GetInt64(3) == 0));
            }

            var indexes = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA index_list(" + Quote(tableName) + ")";
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                    if (rows.GetString(3) == "c")
                        indexes.Add(rows.GetString(1)); // Explicit CREATE INDEX only.
            }

            tables.Add(new SchemaTable(tableName, columns.ToArray(), indexes.ToArray()));
        }

        return tables;
    }

    private static string? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
