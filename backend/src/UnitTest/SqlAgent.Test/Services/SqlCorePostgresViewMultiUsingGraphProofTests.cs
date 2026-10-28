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

/// <summary>Three/four-source PostgreSQL left-deep JOIN USING graph,
/// with stage-visible merged keys and physical-column provenance.</summary>
public sealed class SqlCorePostgresViewMultiUsingGraphProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER NOT NULL, tenant INTEGER NOT NULL, a_note TEXT);" +
        "CREATE TABLE app.b(id INTEGER, tenant INTEGER NOT NULL, b_note TEXT);" +
        "CREATE TABLE app.c(id INTEGER NOT NULL, tenant INTEGER NOT NULL, c_note TEXT);" +
        "CREATE TABLE app.d(id INTEGER, tenant INTEGER NOT NULL, d_note TEXT);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN", "JOIN")]
    [InlineData("LEFT JOIN", "RIGHT JOIN")]
    [InlineData("FULL JOIN", "LEFT JOIN")]
    [InlineData("RIGHT OUTER JOIN", "FULL OUTER JOIN")]
    public void ThreeSourceUsingStarPreservesMergedKeysAndStageNullability(string first, string second)
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT * FROM app.a a " + first +
            " app.b b USING (tenant,id) " + second + " app.c c USING (id,tenant);";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "tenant", "a_note", "b_note", "c_note" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "app.a", "app.b", "app.c" },
            view.Dependencies.Select(d => d!.DisplayName));
        Assert.All(view.SourceBindings, binding =>
        {
            Assert.Contains("id", binding.Columns);
            Assert.Contains("tenant", binding.Columns);
        });
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void FourSourceUsingStarKeepsKeyOrderingAndAllPhysicalJoinDependencies()
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT * FROM app.a a " +
            "JOIN app.b b USING (id,tenant) " +
            "LEFT JOIN app.c c USING (tenant,id) " +
            "FULL JOIN app.d d USING (id,tenant);";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "tenant", "a_note", "b_note", "c_note", "d_note" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(4, view.SourceBindings.Count);
        foreach (var binding in view.SourceBindings)
        {
            Assert.Contains("id", binding.Columns);
            Assert.Contains("tenant", binding.Columns);
        }
        foreach (var table in new[] { "a", "b", "c", "d" })
        {
            foreach (var key in new[] { "id", "tenant" })
            {
                var dropped = Analyze(script + $"ALTER TABLE app.{table} DROP COLUMN {key};");
                Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropped.Statements[5].Status);
                Assert.Empty(dropped.Statements[5].Changes);
            }
        }
    }

    [Fact]
    public void RightJoinAfterLeftJoinSelectsNewRightKeyAndExtendsPriorSources()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id, a.a_note, b.b_note, c.c_note " +
            "FROM app.a a LEFT JOIN app.b b USING (id) " +
            "RIGHT JOIN app.c c USING (id);");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "a_note", "b_note", "c_note" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { false, true, true, true },
            view.Columns.Select(c => c.Nullable));
        Assert.Contains("id", view.SourceBindings[0].Columns);
        Assert.Contains("id", view.SourceBindings[1].Columns);
        Assert.Contains("id", view.SourceBindings[2].Columns);
    }

    [Fact]
    public void FullJoinAfterLeftJoinCoalescesPriorMergedKeyAndNewRightKey()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id AS joined_id, c.id AS physical_c_id " +
            "FROM app.a a LEFT JOIN app.b b USING (id) FULL JOIN app.c c USING (id);");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "joined_id", "physical_c_id" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { false, true }, view.Columns.Select(c => c.Nullable));
        Assert.All(view.SourceBindings, x => Assert.Contains("id", x.Columns));
    }

    [Fact]
    public void ExistingOnEdgeCanFollowUsingWhenItsOperandsAreExplicitlyQualified()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id AS merged_id, a.a_note, c.c_note " +
            "FROM app.a a JOIN app.b b USING (id) " +
            "LEFT JOIN app.c c ON b.id = c.id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "merged_id", "a_note", "c_note" }, view.Columns.Select(c => c.Name));
        Assert.True(view.Columns[2].Nullable);
        Assert.All(view.SourceBindings, x => Assert.Contains("id", x.Columns));
    }

    [Fact]
    public void UsingAfterOnWorksOnlyWhenLeftSubgraphHasUniqueVisibleKey()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL, a_note TEXT);" +
            "CREATE TABLE app.b(a_id INTEGER NOT NULL, b_note TEXT);" +
            "CREATE TABLE app.c(id INTEGER NOT NULL, c_note TEXT);" +
            "CREATE VIEW app.v AS SELECT * FROM app.a a JOIN app.b b ON a.id = b.a_id " +
            "JOIN app.c c USING (id);");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "a_note", "a_id", "b_note", "c_note" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "app.a", "app.b", "app.c" },
            view.Dependencies.Select(d => d!.DisplayName));
        Assert.Contains("a_id", view.SourceBindings[1].Columns);
        Assert.Contains("id", view.SourceBindings[2].Columns);
    }

    [Fact]
    public void ReplaceMultiUsingViewRewiresSourceDependenciesAndKeepsDescendants()
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT id, a.a_note FROM app.a a " +
            "JOIN app.b b USING (id) JOIN app.c c USING (id);" +
            "CREATE VIEW app.child AS SELECT id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT id, a.a_note FROM app.a a " +
            "JOIN app.d d USING (id) JOIN app.c c USING (id);";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[6].Changes).Operation);
        var view = Assert.Single(result.FinalCatalog.Views, v => v.Name == "app.v");
        Assert.Equal(new[] { "app.a", "app.d", "app.c" },
            view.Dependencies.Select(d => d!.DisplayName));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(script +
            "ALTER TABLE app.b DROP COLUMN id;").Statements[7].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(script +
            "ALTER TABLE app.d DROP COLUMN id;").Statements[7].Status);
    }

    [Theory]
    [InlineData("SELECT id FROM app.a a JOIN app.b b ON a.id = b.id JOIN app.c c USING (id)")]
    [InlineData("SELECT * FROM app.a a JOIN app.b b USING (id) JOIN app.c c USING (id,id)")]
    [InlineData("SELECT * FROM app.a a JOIN app.b b USING (id) JOIN app.c c USING (missing)")]
    [InlineData("SELECT * FROM app.a a JOIN app.b b USING (id) JOIN app.c c USING (id) JOIN app.a x USING (id)")]
    [InlineData("SELECT * FROM app.a a JOIN app.b b USING (id) JOIN app.c c USING (id) JOIN app.d d USING (id) JOIN app.a x USING (id)")]
    [InlineData("SELECT * FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON a.id = d.id JOIN app.d d USING (id)")]
    [InlineData("SELECT * FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON id = c.id")]
    [InlineData("SELECT * FROM app.a a FULL JOIN app.b b USING (id) JOIN app.c c USING ()")]
    public void UnprovenMultiUsingNeverPublishesKnownView(string select)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + select + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.DoesNotContain(result.Statements[4].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void LaterUsingTypeMismatchCannotMintPartialKnownView()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL);" +
            "CREATE TABLE app.b(id INTEGER NOT NULL);" +
            "CREATE TABLE app.c(id TEXT);" +
            "CREATE VIEW app.v AS SELECT id FROM app.a a JOIN app.b b USING (id) " +
            "JOIN app.c c USING (id);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsCannotInheritMultiUsingProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT * FROM app.a a JOIN app.b b USING (id) JOIN app.c c USING (id);",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenWorldCatalogCannotClaimMultiUsingView()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT id FROM app.a a JOIN app.b b USING (id) JOIN app.c c USING (id);",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task ThreeSourceMixedOuterUsingRowsAndDropGuardsMatchLivePostgres()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required for live parity.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }
        var schema = "sqlcore_multiu_" + Guid.NewGuid().ToString("N");
        var sql =
            $"CREATE TABLE {schema}.a(id INTEGER NOT NULL, a_note TEXT);" +
            $"CREATE TABLE {schema}.b(id INTEGER, b_note TEXT);" +
            $"CREATE TABLE {schema}.c(id INTEGER NOT NULL, c_note TEXT);" +
            $"CREATE VIEW {schema}.v AS SELECT * FROM {schema}.a a " +
            $"LEFT JOIN {schema}.b b USING (id) FULL JOIN {schema}.c c USING (id);";
        var analyzed = Analyze(sql);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements,
            s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "a_note", "b_note", "c_note" }, view.Columns.Select(c => c.Name));

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Exec(string command)
        {
            await using var cmd = new NpgsqlCommand(command, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Exec("CREATE SCHEMA " + schema);
        foreach (var statement in analyzed.Statements)
            await Exec(statement.Syntax.Text);
        await Exec($"INSERT INTO {schema}.a VALUES (1,'a1'),(2,'a2');");
        await Exec($"INSERT INTO {schema}.b VALUES (1,'b1'),(5,'b5');");
        await Exec($"INSERT INTO {schema}.c VALUES (1,'c1'),(3,'c3');");
        var rows = new List<string>();
        await using (var cmd = new NpgsqlCommand($"SELECT * FROM {schema}.v ORDER BY id", db, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(new[] { "id", "a_note", "b_note", "c_note" },
                Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(reader.GetInt32(0) + "|" +
                    (reader.IsDBNull(1) ? "-" : reader.GetString(1)) + "|" +
                    (reader.IsDBNull(2) ? "-" : reader.GetString(2)) + "|" +
                    (reader.IsDBNull(3) ? "-" : reader.GetString(3)));
        }
        Assert.Equal(new[] { "1|a1|b1|c1", "2|a2|-|-", "3|-|-|c3" }, rows);
        foreach (var table in new[] { "a", "b", "c" })
        {
            await Exec("SAVEPOINT multi_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Exec($"ALTER TABLE {schema}.{table} DROP COLUMN id;"));
            await Exec("ROLLBACK TO SAVEPOINT multi_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
