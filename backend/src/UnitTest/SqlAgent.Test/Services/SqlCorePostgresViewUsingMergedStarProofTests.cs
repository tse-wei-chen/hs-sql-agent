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
/// PostgreSQL INNER JOIN USING unqualified SELECT * requires a merged-column
/// projection, not the concatenated physical source wildcards of JOIN ON.
/// </summary>
public sealed class SqlCorePostgresViewUsingMergedStarProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, tenant INTEGER NOT NULL, title TEXT, region TEXT);" +
        "CREATE TABLE app.lines(id INTEGER NOT NULL, tenant INTEGER NOT NULL, amount BIGINT, active BOOLEAN);";

    private static SqlScriptAnalysisResult Analyze(string script,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(script, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN")]
    [InlineData("INNER JOIN")]
    public void MergedStarUsesUsingClauseKeyOrderBeforeLeftAndRightRemainders(string join)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT * FROM app.orders o " + join +
            " app.lines l USING (tenant,id);");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "tenant", "id", "title", "region", "amount", "active" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "INTEGER", "INTEGER", "TEXT", "TEXT", "BIGINT", "BOOLEAN" },
            view.Columns.Select(c => c.TypeName));
        Assert.Equal(new[] { false, false, true, true, true, true },
            view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "app.orders", "app.lines" },
            view.Dependencies.Select(d => d.DisplayName));
        Assert.Equal(new[] { "tenant", "id", "title", "region" },
            view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "amount", "active", "tenant", "id" },
            view.SourceBindings[1].Columns);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SingleUsingKeyDoesNotDiscardNonUsingPhysicalColumns()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL, left_col TEXT);" +
            "CREATE TABLE app.b(id INTEGER NOT NULL, right_col BOOLEAN);" +
            "CREATE VIEW app.v AS SELECT * FROM app.a a JOIN app.b b USING (id);");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "left_col", "right_col" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "left_col" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "right_col", "id" }, view.SourceBindings[1].Columns);
    }

    [Fact]
    public void ExplicitHeaderRenamesMergedStarColumnsByFinalOrdinal()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (company, public_id, label, area, total, enabled) AS " +
            "SELECT * FROM app.orders o JOIN app.lines l USING (tenant,id);");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "company", "public_id", "label", "area", "total", "enabled" },
            view.Columns.Select(c => c.Name));
        Assert.Equal("BIGINT", view.Columns[4].TypeName);
        Assert.Equal(new[] { "tenant", "id", "title", "region" },
            view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "amount", "active", "tenant", "id" },
            view.SourceBindings[1].Columns);
    }

    [Fact]
    public void OwnedWildcardDependenciesBlockBothPhysicalUsingKeysAndAllProjectedColumns()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT * FROM app.orders o JOIN app.lines l USING (tenant,id);" +
            "CREATE VIEW app.child AS SELECT tenant, active FROM app.v;";
        foreach (var table in new[] { "orders", "lines" })
        foreach (var col in new[] { "tenant", "id" })
        {
            var result = Analyze(setup + $"ALTER TABLE app.{table} DROP COLUMN {col};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[4].Status);
            Assert.Empty(result.Statements[4].Changes);
        }
        foreach (var (table, col) in new[] { ("orders", "title"), ("lines", "amount") })
        {
            var result = Analyze(setup + $"ALTER TABLE app.{table} DROP COLUMN {col};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[4].Status);
            Assert.Empty(result.Statements[4].Changes);
        }
        var cascade = Analyze(setup + "DROP TABLE app.lines CASCADE;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, cascade.Statements[4].Status);
        Assert.Empty(cascade.FinalCatalog.Views);
    }

    [Fact]
    public void ReplaceUsingStarPreservesOutputShapeAndPhysicalDependencies()
    {
        var sql = Tables +
            "CREATE TABLE app.backup(id INTEGER NOT NULL, tenant INTEGER NOT NULL, title TEXT, region TEXT);" +
            "CREATE VIEW app.v AS SELECT * FROM app.orders o JOIN app.lines l USING (tenant,id);" +
            "CREATE VIEW app.child AS SELECT tenant FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT * FROM app.backup b JOIN app.lines l USING (tenant,id);";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[5].Changes).Operation);
        var view = result.FinalCatalog.Views.Single(v => v.Name == "app.v");
        Assert.Equal(new[] { "app.backup", "app.lines" },
            view.Dependencies.Select(d => d.DisplayName));
        Assert.Equal(new[] { "tenant", "id", "title", "region", "amount", "active" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(sql +
            "ALTER TABLE app.orders DROP COLUMN id;").Statements[6].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(sql +
            "ALTER TABLE app.backup DROP COLUMN id;").Statements[6].Status);
    }

    [Fact]
    public void EightUsingKeysAreBoundInDeclaredOrderWithoutDroppingEitherPhysicalOwner()
    {
        var keys = Enumerable.Range(1, 8).Select(i => "k" + i).ToArray();
        var columns = string.Join(",", keys.Select(k => k + " INTEGER NOT NULL"));
        var order = string.Join(",", keys.Reverse());
        var result = Analyze(
            $"CREATE TABLE app.a({columns},left_data TEXT);" +
            $"CREATE TABLE app.b({columns},right_data BOOLEAN);" +
            $"CREATE VIEW app.v AS SELECT * FROM app.a a JOIN app.b b USING ({order});");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(keys.Reverse().Concat(new[] { "left_data", "right_data" }),
            view.Columns.Select(c => c.Name));
        Assert.Equal(keys.Reverse().Concat(new[] { "left_data" }), view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "right_data" }.Concat(keys.Reverse()), view.SourceBindings[1].Columns);
    }

    [Theory]
    [InlineData("SELECT * FROM app.orders o LEFT JOIN app.lines l USING (id)")]
    [InlineData("SELECT * FROM app.orders o RIGHT JOIN app.lines l USING (id)")]
    [InlineData("SELECT * FROM app.orders o FULL JOIN app.lines l USING (id)")]
    [InlineData("SELECT * FROM app.orders o JOIN app.lines l USING (id) JOIN app.other x USING (id)")]
    [InlineData("SELECT * FROM app.orders o JOIN app.orders x USING (id)")]
    [InlineData("SELECT * FROM app.orders o JOIN app.lines l USING (id,id)")]
    [InlineData("SELECT * FROM app.orders o JOIN app.lines l USING (missing)")]
    [InlineData("SELECT * FROM app.orders o JOIN app.lines l USING (id,missing)")]
    [InlineData("SELECT * FROM app.orders o JOIN app.lines l USING ()")]
    public void UnprovenUsingStarNeverPublishesKnownView(string query)
    {
        var result = Analyze(Tables +
            "CREATE TABLE app.other(id INTEGER NOT NULL);" +
            "CREATE VIEW app.v AS " + query + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void DuplicateNonKeyOutputNamesRemainInvalidRatherThanSilentlyMerged()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL, clash TEXT);" +
            "CREATE TABLE app.b(id INTEGER NOT NULL, clash TEXT);" +
            "CREATE VIEW app.v AS SELECT * FROM app.a a JOIN app.b b USING (id);");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void IncompatibleKeyTypesRemainFailClosedForWildcard()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL, a_note TEXT);" +
            "CREATE TABLE app.b(id TEXT, b_note TEXT);" +
            "CREATE VIEW app.v AS SELECT * FROM app.a a JOIN app.b b USING (id);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsCannotInheritPostgresMergedStarProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT * FROM app.orders o JOIN app.lines l USING (id);",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnknownCatalogCannotClaimMergedStarProof()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT * FROM app.orders o JOIN app.lines l USING (id);",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task MergedStarColumnOrderingTypesAndRowsMatchLivePostgres()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required for PostgreSQL parity.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }

        var schema = "sqlcore_star_" + Guid.NewGuid().ToString("N");
        var create =
            $"CREATE TABLE {schema}.orders(id INTEGER NOT NULL, tenant INTEGER NOT NULL, title TEXT, region TEXT);" +
            $"CREATE TABLE {schema}.lines(id INTEGER NOT NULL, tenant INTEGER NOT NULL, amount BIGINT, active BOOLEAN);" +
            $"CREATE VIEW {schema}.v AS SELECT * FROM {schema}.orders o JOIN {schema}.lines l USING (tenant,id);";
        var analysis = Analyze(create);
        Assert.Empty(analysis.Diagnostics);
        Assert.All(analysis.Statements, stmt =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, stmt.Status));
        var view = Assert.Single(analysis.FinalCatalog.Views);
        var expectedNames = new[] { "tenant", "id", "title", "region", "amount", "active" };
        Assert.Equal(expectedNames, view.Columns.Select(c => c.Name));

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Execute("CREATE SCHEMA " + schema);
        foreach (var statement in analysis.Statements)
            await Execute(statement.Syntax.Text);
        var physicalNames = new List<string>();
        var physicalTypes = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns " +
            "WHERE table_schema = @schema AND table_name = 'v' ORDER BY ordinal_position", db, tx))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                physicalNames.Add(reader.GetString(0));
                physicalTypes.Add(reader.GetString(1));
            }
        }
        Assert.Equal(expectedNames, physicalNames);
        Assert.Equal(new[] { "integer", "integer", "text", "text", "bigint", "boolean" },
            physicalTypes);
        await Execute($"INSERT INTO {schema}.orders VALUES (1,7,'ready','north'),(2,7,'unmatched','south');");
        await Execute($"INSERT INTO {schema}.lines VALUES (1,7,101,true),(2,8,999,false);");
        await using (var cmd = new NpgsqlCommand($"SELECT * FROM {schema}.v", db, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(7, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal("ready", reader.GetString(2));
            Assert.Equal("north", reader.GetString(3));
            Assert.Equal(101L, reader.GetInt64(4));
            Assert.True(reader.GetBoolean(5));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        foreach (var relation in new[] { "orders", "lines" })
        {
            await Execute("SAVEPOINT star_dependency");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Execute($"ALTER TABLE {schema}.{relation} DROP COLUMN tenant;"));
            await Execute("ROLLBACK TO SAVEPOINT star_dependency");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DuplicateNonKeyViewColumnNamesFailInRealPostgresToo()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required for PostgreSQL parity.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }
        var schema = "sqlcore_starneg_" + Guid.NewGuid().ToString("N");
        var setup =
            $"CREATE TABLE {schema}.a(id INTEGER, clash TEXT);" +
            $"CREATE TABLE {schema}.b(id INTEGER, clash TEXT);";
        var bad = $"CREATE VIEW {schema}.v AS SELECT * FROM {schema}.a a JOIN {schema}.b b USING (id);";
        var analyzed = Analyze(setup + bad);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, analyzed.Statements[2].Status);
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var cmd = new NpgsqlCommand("CREATE SCHEMA " + schema, db, tx))
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        foreach (var statement in analyzed.Statements.Take(2))
        {
            await using var cmd = new NpgsqlCommand(statement.Syntax.Text, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var cmd = new NpgsqlCommand(bad, db, tx))
            await Assert.ThrowsAsync<PostgresException>(() =>
                cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
