using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Executes proven static DDL against a real SQL Server 2022 instance in an
/// isolated database, then compares with independently read sys.* metadata.
/// The SQL analyzer does not open connections or execute any of the SQL.
/// </summary>
public sealed class SqlCoreSqlServerLiveDdlParityTests
{
    private sealed record Column(string Name, string Type, bool Nullable);
    private sealed record Table(string Name, Column[] Columns, string[] Indexes);

    private static readonly (string Id, string Sql)[] Scenarios =
    [
        ("inventory_add", "CREATE TABLE inventory(id INT NOT NULL, sku NVARCHAR(64)); ALTER TABLE inventory ADD qty INT;"),
        ("shipping_drop_column", "CREATE TABLE shipping(id INT, description NVARCHAR(64)); ALTER TABLE shipping DROP COLUMN description;"),
        ("invoices_add_drop", "CREATE TABLE invoices(id INT, status NVARCHAR(64)); ALTER TABLE invoices ADD amount INT; ALTER TABLE invoices DROP COLUMN status;"),
        ("two_tables", "CREATE TABLE purchases(id INT); CREATE TABLE refunds(id INT, purchase_id INT);"),
        ("drop_second", "CREATE TABLE customers(id INT); CREATE TABLE addresses(id INT); DROP TABLE addresses;"),
        ("drop_recreate", "CREATE TABLE products(id INT); DROP TABLE products; CREATE TABLE products(sku NVARCHAR(64) NOT NULL);"),
        ("conditional_drop", "CREATE TABLE users(id INT); DROP TABLE IF EXISTS users; DROP TABLE IF EXISTS users;"),
        ("index_kept", "CREATE TABLE events(id INT, state NVARCHAR(64)); CREATE INDEX idx_events_state ON events(state);"),
        ("index_removed", "CREATE TABLE events(id INT, state NVARCHAR(64)); CREATE INDEX idx_events_state ON events(state); DROP INDEX idx_events_state ON events;"),
        ("two_indexes", "CREATE TABLE locations(id INT, city NVARCHAR(64), region NVARCHAR(64)); CREATE INDEX idx_locations_city ON locations(city); CREATE INDEX idx_locations_region ON locations(region);"),
        ("index_table_drop", "CREATE TABLE jobs(id INT, status NVARCHAR(64)); CREATE INDEX idx_jobs_status ON jobs(status); DROP TABLE jobs;"),
        ("quoted_simple", "CREATE TABLE [order_items]([id] INT NOT NULL, [label] NVARCHAR(64)); ALTER TABLE [order_items] ADD [amount] INT;"),
        ("alter_then_index", "CREATE TABLE reports(id INT); ALTER TABLE reports ADD tag NVARCHAR(64); CREATE INDEX idx_reports_tag ON reports(tag);"),
        ("index_drop_then_column", "CREATE TABLE balances(id INT, memo NVARCHAR(64)); CREATE INDEX idx_balances_memo ON balances(memo); DROP INDEX idx_balances_memo ON balances; ALTER TABLE balances DROP COLUMN memo;"),
        ("drop_and_keep", "CREATE TABLE scratch(id INT); CREATE TABLE records(id INT, note NVARCHAR(64)); DROP TABLE scratch; ALTER TABLE records ADD active BIT;"),
        ("transaction_rollback", "BEGIN TRAN; CREATE TABLE transient(id INT); ROLLBACK TRAN;"),
        ("transaction_commit", "BEGIN TRAN; CREATE TABLE permanent(id INT); COMMIT TRAN;")
    ];

    public static IEnumerable<object[]> Cases =>
        Scenarios.Select(x => new object[] { x.Id });

    [Fact]
    public void CorpusContainsSeventeenNonidenticalSqlServerWorkflows()
    {
        Assert.Equal(17, Scenarios.Length);
        Assert.Equal(17, Scenarios.Select(x => x.Id).Distinct().Count());
        Assert.Equal(17, Scenarios.Select(x => x.Sql).Distinct().Count());
        Assert.All(Scenarios, x => Assert.True(x.Sql.Count(c => c == ';') >= 2));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryProvenCatalogTransitionMatchesRealSqlServer(string scenario)
    {
        var root = ConnectionString();
        var sql = Scenarios.Single(x => x.Id == scenario).Sql;
        // Version 16 is the SQL Server 2022 test image, and is required for
        // IF EXISTS native DDL capability (SQL Server 2016+).
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer, new Version(16, 0));
        var analysis = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.MsSqlServer,
            SqlCatalogState.KnownEmpty(), profile);
        Assert.True(analysis.Statements.Count >= 2);
        Assert.All(analysis.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        Assert.Empty(analysis.Diagnostics);

        var database = "sqlcore_p6_" + Guid.NewGuid().ToString("N");
        await using var admin = new SqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await Run(admin, "CREATE DATABASE [" + database + "]",
            TestContext.Current.CancellationToken);
        try
        {
            var isolated = new SqlConnectionStringBuilder(root)
            {
                InitialCatalog = database, Pooling = false
            }.ConnectionString;
            await using (var db = new SqlConnection(isolated))
            {
                await db.OpenAsync(TestContext.Current.CancellationToken);
                for (var i = 0; i < analysis.Statements.Count; i++)
                {
                    var stmt = analysis.Statements[i];
                    Assert.True(stmt.CatalogAfter.IsClosedWorld, $"{scenario}#{i}");
                    await Run(db, stmt.Syntax.Text, TestContext.Current.CancellationToken);
                    AssertEquivalent(scenario, i,
                        stmt.CatalogAfter, await ReadSchema(db));
                }
                AssertEquivalent(scenario, analysis.Statements.Count,
                    analysis.FinalCatalog, await ReadSchema(db));
            }
        }
        finally
        {
            // DDL may commit, so deletion—not transaction rollback—is the
            // reliable SQL Server schema cleanup boundary.
            await Run(admin,
                "ALTER DATABASE [" + database + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                CancellationToken.None);
            await Run(admin, "DROP DATABASE [" + database + "]",
                CancellationToken.None);
        }
    }

    [Fact]
    public void UnsupportedCheckConstraintNeverBecomesCertifiedDDL()
    {
        const string sql = "CREATE TABLE constrained(id INT, CHECK (id > 0));";
        var analysis = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            Assert.Single(analysis.Statements).Status);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlEffectCoverage.Unknown, analysis.EffectCoverage);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "SQL Server DDL parity in CI requires TEST_SQLSERVER_CONNECTION.");
        Assert.Skip("Live SQL Server DDL parity requires TEST_SQLSERVER_CONNECTION.");
        throw new InvalidOperationException("Skipped.");
    }

    private static async Task Run(SqlConnection db, string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<IReadOnlyList<Table>> ReadSchema(SqlConnection connection)
    {
        var names = new List<(int Id, string Name)>();
        await using (var cmd = new SqlCommand(
            "SELECT t.object_id, t.name FROM sys.tables t " +
            "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "WHERE s.name = 'dbo' AND t.is_ms_shipped = 0 ORDER BY t.name", connection))
        {
            await using var reader = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                names.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        var result = new List<Table>();
        foreach (var (id, name) in names)
        {
            var columns = new List<Column>();
            await using (var cmd = new SqlCommand(
                "SELECT c.name, ty.name, c.max_length, c.is_nullable " +
                "FROM sys.columns c JOIN sys.types ty ON c.user_type_id = ty.user_type_id " +
                "WHERE c.object_id = @object ORDER BY c.column_id", connection))
            {
                cmd.Parameters.AddWithValue("@object", id);
                await using var reader = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    var nameColumn = reader.GetString(0);
                    var type = reader.GetString(1);
                    var maxLength = reader.GetInt16(2);
                    if (string.Equals(type, "nvarchar", StringComparison.OrdinalIgnoreCase))
                        type += "(" + (maxLength / 2) + ")";
                    columns.Add(new Column(nameColumn, type, reader.GetBoolean(3)));
                }
            }

            var indexes = new List<string>();
            await using (var cmd = new SqlCommand(
                "SELECT name FROM sys.indexes WHERE object_id = @object " +
                "AND name IS NOT NULL AND index_id > 0 " +
                "AND is_primary_key = 0 AND is_unique_constraint = 0 ORDER BY name", connection))
            {
                cmd.Parameters.AddWithValue("@object", id);
                await using var reader = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                    indexes.Add(reader.GetString(0));
            }
            result.Add(new Table(name, columns.ToArray(), indexes.ToArray()));
        }
        return result;
    }

    private static void AssertEquivalent(string scenario, int step,
        SqlCatalogState expected, IReadOnlyList<Table> actual)
    {
        Assert.True(expected.IsClosedWorld);
        Assert.Equal(expected.Tables.Count, actual.Count);
        foreach (var table in expected.Tables)
        {
            Assert.Equal(SqlCatalogKnowledge.Known, table.Knowledge);
            var live = Assert.Single(actual.Where(x =>
                string.Equals(x.Name, table.Name, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(table.Columns.Count, live.Columns.Length);
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var wanted = table.Columns[c];
                var got = live.Columns[c];
                Assert.True(string.Equals(wanted.Name, got.Name, StringComparison.OrdinalIgnoreCase),
                    $"{scenario}#{step} expected {wanted.Name} but got {got.Name}");
                Assert.True(string.Equals(wanted.TypeName, got.Type, StringComparison.OrdinalIgnoreCase),
                    $"{scenario}#{step}/{table.Name}.{wanted.Name}: expected {wanted.TypeName} but SQL Server says {got.Type}");
                Assert.Equal(wanted.Nullable, got.Nullable);
            }
            Assert.Equal(
                table.Indexes.Select(x => x.Name).Order(StringComparer.OrdinalIgnoreCase),
                live.Indexes.Order(StringComparer.OrdinalIgnoreCase));
        }
    }
}
