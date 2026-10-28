using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Independent live PostgreSQL 16 DDL differential oracle. Each modeled schema
/// transition is compared with information_schema and pg_indexes. The analyzer
/// never connects to a database: only the test harness executes its SQL.
/// </summary>
public sealed class SqlCorePostgresLiveDdlParityTests
{
    private sealed record Column(string Name, string DataType, bool Nullable);
    private sealed record Table(string Name, Column[] Columns, string[] Indexes);

    private static readonly (string Name, string Script)[] Scenarios =
    [
        ("create_add", "CREATE TABLE inventory (id INTEGER NOT NULL, sku TEXT); ALTER TABLE inventory ADD COLUMN qty INTEGER;"),
        ("create_add_drop_column", "CREATE TABLE orders (id INTEGER NOT NULL, note TEXT); ALTER TABLE orders ADD COLUMN status TEXT; ALTER TABLE orders DROP COLUMN note;"),
        ("two_tables", "CREATE TABLE invoices (id INTEGER, amount INTEGER); CREATE TABLE payments (id INTEGER, invoice_id INTEGER);"),
        ("two_tables_drop_one", "CREATE TABLE customers (id INTEGER); CREATE TABLE addresses (id INTEGER, customer_id INTEGER); DROP TABLE addresses;"),
        ("drop_recreate", "CREATE TABLE products (id INTEGER); DROP TABLE products; CREATE TABLE products (code TEXT NOT NULL);"),
        ("conditional_create_noop", "CREATE TABLE users (id INTEGER); CREATE TABLE IF NOT EXISTS users (other TEXT);"),
        ("conditional_drop_noop", "CREATE TABLE users (id INTEGER); DROP TABLE IF EXISTS users; DROP TABLE IF EXISTS users;"),
        ("index_create_drop", "CREATE TABLE events (id INTEGER NOT NULL, state TEXT); CREATE INDEX idx_events_state ON events(state); DROP INDEX idx_events_state;"),
        ("index_kept", "CREATE TABLE shipments (id INTEGER NOT NULL, region TEXT); CREATE INDEX idx_shipments_region ON shipments(region);"),
        ("index_table_drop", "CREATE TABLE jobs (id INTEGER, region TEXT); CREATE INDEX idx_jobs_region ON jobs(region); DROP TABLE jobs;"),
        ("two_indexes", "CREATE TABLE listings (id INTEGER, seller TEXT, status TEXT); CREATE INDEX idx_listings_seller ON listings(seller); CREATE INDEX idx_listings_status ON listings(status);"),
        ("quoted_simple", "CREATE TABLE \"order_items\" (\"id\" INTEGER NOT NULL, \"label\" TEXT); ALTER TABLE \"order_items\" ADD COLUMN \"amount\" INTEGER;"),
        ("quoted_dotted", "CREATE TABLE \"log.entries\" (\"id\" INTEGER); CREATE TABLE log (id INTEGER);"),
        ("add_then_index", "CREATE TABLE readings (id INTEGER, value INTEGER); ALTER TABLE readings ADD COLUMN unit TEXT; CREATE INDEX idx_readings_unit ON readings(unit);"),
        ("create_drop_alter", "CREATE TABLE sessions (id INTEGER); CREATE TABLE tokens (id INTEGER, session_id INTEGER); ALTER TABLE sessions ADD COLUMN label TEXT; DROP TABLE tokens;"),
        ("column_drop_if_exists", "CREATE TABLE reports (id INTEGER, note TEXT); ALTER TABLE reports DROP COLUMN IF EXISTS missing; ALTER TABLE reports DROP COLUMN note;"),
        ("index_drop_then_column", "CREATE TABLE audit (id INTEGER, event TEXT); CREATE INDEX idx_audit_event ON audit(event); DROP INDEX idx_audit_event; ALTER TABLE audit DROP COLUMN event;"),
        ("index_after_recreate", "CREATE TABLE sales (id INTEGER); DROP TABLE sales; CREATE TABLE sales (code TEXT); CREATE INDEX idx_sales_code ON sales(code);")
    ];

    public static IEnumerable<object[]> Cases =>
        Scenarios.Select(x => new object[] { x.Name });

    [Fact]
    public void RegressionCorpusHasEighteenDistinctPersistentWorkflows()
    {
        Assert.Equal(18, Scenarios.Length);
        Assert.Equal(18, Scenarios.Select(x => x.Name).Distinct().Count());
        Assert.Equal(18, Scenarios.Select(x => x.Script).Distinct().Count());
        Assert.All(Scenarios, s => Assert.True(s.Script.Count(x => x == ';') >= 2));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EachProvenCatalogTransitionMatchesIndependentPostgresSchema(string id)
    {
        var connectionString = GetConnectionString();
        var script = Scenarios.Single(s => s.Name == id).Script;
        var analysis = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.True(analysis.Statements.Count >= 2, id);
        Assert.All(analysis.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Empty(analysis.Diagnostics);

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        // A unique schema is created INSIDE the rollback-only transaction.
        // Zero shared catalog pollution, no dependence on public/user search_path.
        var schema = "sqlcore_p6_" + Guid.NewGuid().ToString("N");
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        await Execute(db, tx, "SET LOCAL search_path TO " + schema);

        for (var index = 0; index < analysis.Statements.Count; index++)
        {
            var stmt = analysis.Statements[index];
            Assert.True(stmt.CatalogAfter.IsClosedWorld,
                $"{id}#{index}: claimed DDL support must prove virtual Catalog shape");
            await Execute(db, tx, stmt.Syntax.Text);
            AssertEquivalent(id, index, stmt.CatalogAfter, await ReadSchema(db, tx, schema));
        }
        AssertEquivalent(id, analysis.Statements.Count,
            analysis.FinalCatalog, await ReadSchema(db, tx, schema));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("BEGIN; CREATE TABLE transient (id INTEGER); ROLLBACK;", false)]
    [InlineData("BEGIN; CREATE TABLE permanent (id INTEGER); COMMIT;", true)]
    public async Task TransactionalDdlStateMatchesActualRollbackAndCommit(
        string script, bool tableRemains)
    {
        var connectionString = GetConnectionString();
        var analysis = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.All(analysis.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        var schema = "sqlcore_p6_" + Guid.NewGuid().ToString("N");
        await Execute(db, null, "CREATE SCHEMA " + schema);
        try
        {
            await Execute(db, null, "SET search_path TO " + schema);
            for (var index = 0; index < analysis.Statements.Count; index++)
            {
                var statement = analysis.Statements[index];
                await Execute(db, null, statement.Syntax.Text);
                AssertEquivalent("transaction", index, statement.CatalogAfter,
                    await ReadSchema(db, null, schema));
            }
            Assert.Equal(tableRemains, analysis.FinalCatalog.Tables.Count > 0);
            AssertEquivalent("transaction-final", analysis.Statements.Count,
                analysis.FinalCatalog, await ReadSchema(db, null, schema));
        }
        finally
        {
            // Unique schema name is generated locally, never from SQL input.
            // Schema teardown must execute even when the parity assertion fails.
            await Execute(db, null, "RESET search_path");
            await Execute(db, null, "DROP SCHEMA " + schema + " CASCADE");
        }
    }

    [Fact]
    public void PostgresSupportsCheckConstraintsButAnalyzerDoesNotClaimTheirProof()
    {
        const string sql = "CREATE TABLE checked (id INTEGER, CHECK(id > 0));";
        var result = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlEffectCoverage.Unknown, result.EffectCoverage);
        // This particular capability is intentionally NOT added to the proven
        // differential set. The live-provider gate must not expand by guesswork.
    }

    [Fact]
    public async Task ReplacementDependencyRewiringMatchesLivePostgres()
    {
        var schema = "sqlcore_vr_" + Guid.NewGuid().ToString("N");
        var setup =
            $"CREATE TABLE {schema}.original(id INTEGER, note TEXT);" +
            $"CREATE TABLE {schema}.replacement(id INTEGER, note TEXT);" +
            $"CREATE VIEW {schema}.v AS SELECT id AS exposed FROM {schema}.original;" +
            $"CREATE VIEW {schema}.child AS SELECT exposed AS child_id FROM {schema}.v;" +
            $"CREATE OR REPLACE VIEW {schema}.v AS SELECT id AS exposed FROM {schema}.replacement;";
        var analysis = SqlCoreFacade.AnalyzeScript(
            setup, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(analysis.Diagnostics);
        Assert.All(analysis.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(analysis.Statements[4].Changes).Operation);
        Assert.Equal(schema + ".replacement",
            Assert.Single(analysis.FinalCatalog.Views
                .Single(v => v.Name == schema + ".v").Dependencies).DisplayName);

        var oldColumnDrop = $"ALTER TABLE {schema}.original DROP COLUMN id;";
        var newColumnDrop = $"ALTER TABLE {schema}.replacement DROP COLUMN id;";
        var staleDependencyCheck = SqlCoreFacade.AnalyzeScript(
            setup + oldColumnDrop, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, staleDependencyCheck.Statements[5].Status);
        var activeDependencyCheck = SqlCoreFacade.AnalyzeScript(
            setup + newColumnDrop, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, activeDependencyCheck.Statements[5].Status);
        Assert.Empty(activeDependencyCheck.Statements[5].Changes);

        await using var db = new NpgsqlConnection(GetConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        foreach (var statement in analysis.Statements)
            await Execute(db, tx, statement.Syntax.Text);

        // PostgreSQL's dependency rewrite must release the former source.
        await Execute(db, tx, oldColumnDrop);
        // And it must still block the currently referenced physical source.
        await Assert.ThrowsAsync<PostgresException>(() => Execute(db, tx, newColumnDrop));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReplacingViewWithDifferentOutputTypeIsRejectedByPostgresToo()
    {
        var schema = "sqlcore_vr_" + Guid.NewGuid().ToString("N");
        var create =
            $"CREATE TABLE {schema}.src(id INTEGER, note TEXT);" +
            $"CREATE VIEW {schema}.v AS SELECT id AS exposed FROM {schema}.src;";
        var incompatible =
            $"CREATE OR REPLACE VIEW {schema}.v AS SELECT note AS exposed FROM {schema}.src;";
        var staticResult = SqlCoreFacade.AnalyzeScript(
            create + incompatible, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, staticResult.Statements[2].Status);
        Assert.Empty(staticResult.Statements[2].Changes);
        Assert.True(staticResult.FinalCatalog.IsClosedWorld);

        await using var db = new NpgsqlConnection(GetConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        await Execute(db, tx, staticResult.Statements[0].Syntax.Text);
        await Execute(db, tx, staticResult.Statements[1].Syntax.Text);
        await Assert.ThrowsAsync<PostgresException>(() => Execute(db, tx, incompatible));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExplicitViewHeadersAndCompatibleReplacementMatchActualPostgresColumns()
    {
        var schema = "sqlcore_vh_" + Guid.NewGuid().ToString("N");
        var script =
            $"CREATE TABLE {schema}.src(id INTEGER NOT NULL, label TEXT, extra INTEGER);" +
            $"CREATE TABLE {schema}.alt(id INTEGER NOT NULL, label TEXT, extra INTEGER);" +
            $"CREATE VIEW {schema}.v (public_id, public_label) " +
            $"AS SELECT id AS ignored, label AS ignored FROM {schema}.src;" +
            $"CREATE VIEW {schema}.child (child_id) AS SELECT public_id FROM {schema}.v;" +
            $"CREATE OR REPLACE VIEW {schema}.v (public_id, public_label, trailing) " +
            $"AS SELECT id, label, extra FROM {schema}.alt;";
        var staticResult = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(staticResult.Diagnostics);
        Assert.All(staticResult.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(staticResult.Statements[4].Changes).Operation);
        Assert.Equal(new[] { "public_id", "public_label", "trailing" },
            staticResult.FinalCatalog.Views.Single(v => v.Name == schema + ".v")
                .Columns.Select(column => column.Name));
        Assert.Equal(new[] { "id", "label", "extra" },
            staticResult.FinalCatalog.Views.Single(v => v.Name == schema + ".v").SourceColumns);

        await using var db = new NpgsqlConnection(GetConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        foreach (var statement in staticResult.Statements)
            await Execute(db, tx, statement.Syntax.Text);

        var names = new List<string>();
        var types = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns " +
            "WHERE table_schema = @schema AND table_name = 'v' ORDER BY ordinal_position",
            db, tx))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            await using var rows = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await rows.ReadAsync(TestContext.Current.CancellationToken))
            {
                names.Add(rows.GetString(0));
                types.Add(rows.GetString(1));
            }
        }
        Assert.Equal(new[] { "public_id", "public_label", "trailing" }, names);
        Assert.Equal(new[] { "integer", "text", "integer" }, types);

        // The physical source-column proof must follow the newly replaced
        // source, not the user-visible header aliases or the old source.
        await Execute(db, tx, $"ALTER TABLE {schema}.src DROP COLUMN id;");
        await Assert.ThrowsAsync<PostgresException>(() =>
            Execute(db, tx, $"ALTER TABLE {schema}.alt DROP COLUMN id;"));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("(only_one)", "SELECT id, label")]
    [InlineData("(one, two, three)", "SELECT id, label")]
    [InlineData("(repeated, repeated)", "SELECT id, label")]
    public async Task InvalidViewHeadersFailClosedAndLivePostgresRejectsThem(
        string header, string select)
    {
        var schema = "sqlcore_vh_" + Guid.NewGuid().ToString("N");
        var table = $"CREATE TABLE {schema}.src(id INTEGER NOT NULL, label TEXT);";
        var invalid = $"CREATE VIEW {schema}.bad {header} AS {select} FROM {schema}.src;";
        var staticResult = SqlCoreFacade.AnalyzeScript(
            table + invalid, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, staticResult.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, staticResult.Statements[1].Status);
        Assert.Empty(staticResult.Statements[1].Changes);
        Assert.True(staticResult.FinalCatalog.IsClosedWorld);

        await using var db = new NpgsqlConnection(GetConnectionString());
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await Execute(db, tx, "CREATE SCHEMA " + schema);
        await Execute(db, tx, table);
        await Assert.ThrowsAsync<PostgresException>(() => Execute(db, tx, invalid));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static string GetConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "TEST_POSTGRES_CONNECTION is required for real PostgreSQL DDL parity in CI.");
        Assert.Skip("Live PostgreSQL DDL parity needs TEST_POSTGRES_CONNECTION.");
        throw new InvalidOperationException("Unreachable: skipped test.");
    }

    private static async Task Execute(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<Table>> ReadSchema(
        NpgsqlConnection db, NpgsqlTransaction? tx, string schema)
    {
        var tables = new List<Table>();
        var names = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT tablename FROM pg_catalog.pg_tables " +
            "WHERE schemaname = @schema ORDER BY tablename", db, tx))
        {
            list.Parameters.AddWithValue("schema", schema);
            await using var rows = await list.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await rows.ReadAsync(TestContext.Current.CancellationToken))
                names.Add(rows.GetString(0));
        }
        foreach (var name in names)
        {
            var columns = new List<Column>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT column_name, data_type, is_nullable FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position", db, tx))
            {
                cmd.Parameters.AddWithValue("schema", schema);
                cmd.Parameters.AddWithValue("table", name);
                await using var rows = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await rows.ReadAsync(TestContext.Current.CancellationToken))
                    columns.Add(new Column(rows.GetString(0), rows.GetString(1),
                        rows.GetString(2) == "YES"));
            }
            var indexes = new List<string>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT indexname FROM pg_catalog.pg_indexes " +
                "WHERE schemaname = @schema AND tablename = @table ORDER BY indexname", db, tx))
            {
                cmd.Parameters.AddWithValue("schema", schema);
                cmd.Parameters.AddWithValue("table", name);
                await using var rows = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await rows.ReadAsync(TestContext.Current.CancellationToken))
                    indexes.Add(rows.GetString(0));
            }
            tables.Add(new Table(name, columns.ToArray(), indexes.ToArray()));
        }
        return tables;
    }

    private static void AssertEquivalent(string id, int statementIndex,
        SqlCatalogState model, IReadOnlyList<Table> live)
    {
        Assert.True(model.IsClosedWorld, $"{id}#{statementIndex}: virtual Catalog not closed");
        Assert.Equal(model.Tables.Count, live.Count);
        foreach (var table in model.Tables)
        {
            Assert.Equal(SqlCatalogKnowledge.Known, table.Knowledge);
            var physical = Assert.Single(live.Where(
                x => string.Equals(x.Name, table.Name, StringComparison.Ordinal)));
            Assert.Equal(table.Columns.Count, physical.Columns.Length);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var expected = table.Columns[i];
                var actual = physical.Columns[i];
                Assert.Equal(expected.Name, actual.Name);
                Assert.True(string.Equals(expected.TypeName, actual.DataType,
                        StringComparison.OrdinalIgnoreCase),
                    $"{id}#{statementIndex}/{table.Name}.{expected.Name}: " +
                    $"{expected.TypeName} vs {actual.DataType}");
                Assert.Equal(expected.Nullable, actual.Nullable);
            }
            Assert.Equal(table.Indexes.Select(x => x.Name).Order(StringComparer.Ordinal),
                physical.Indexes.Order(StringComparer.Ordinal));
        }
    }
}
