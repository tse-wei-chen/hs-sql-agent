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

/// <summary>Same-side ON predicates are legal SQL and must keep
/// the LEFT/RIGHT/FULL ON semantics, column dependencies, stage scope,
/// and their distinct role from the cross-edge JOIN comparisons.</summary>
public sealed class SqlCorePostgresViewOnSameSideProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER NOT NULL, tenant INTEGER NOT NULL, region INTEGER, score INTEGER, a_note TEXT NOT NULL);" +
        "CREATE TABLE app.b(id INTEGER NOT NULL, tenant INTEGER NOT NULL, region INTEGER, score INTEGER, b_note TEXT NOT NULL);" +
        "CREATE TABLE app.c(parent_id INTEGER NOT NULL, tenant INTEGER, region INTEGER, c_note TEXT NOT NULL);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN")]
    [InlineData("LEFT JOIN")]
    [InlineData("RIGHT OUTER JOIN")]
    [InlineData("FULL JOIN")]
    public void OnAndAcceptsBothLeftAndRightLocalTypedPredicates(string kind)
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT a.id AS a_id, b.id AS b_id " +
            "FROM app.a a " + kind + " app.b b " +
            "ON a.id = b.id AND a.tenant = a.region AND b.score >= b.region;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "a_id", "b_id" }, view.Columns.Select(x => x.Name));
        Assert.Equal(new[] { kind is "RIGHT OUTER JOIN" or "FULL JOIN",
                             kind is "LEFT JOIN" or "FULL JOIN" },
                     view.Columns.Select(x => x.Nullable));
        Assert.Contains("tenant", view.SourceBindings[0].Columns);
        Assert.Contains("region", view.SourceBindings[0].Columns);
        Assert.Contains("score", view.SourceBindings[1].Columns);
        Assert.Contains("region", view.SourceBindings[1].Columns);
    }

    [Theory]
    [InlineData("a.id = b.id AND (a.tenant = a.region OR b.tenant = b.region)")]
    [InlineData("(a.id = b.id AND a.tenant >= a.region) OR b.score > b.region")]
    [InlineData("(a.id IS NOT DISTINCT FROM b.id) AND (a.tenant IS DISTINCT FROM a.region)")]
    [InlineData("a.id = b.id AND b.tenant IS NOT DISTINCT FROM b.region")]
    [InlineData("b.region < b.score OR (b.id = a.id AND a.tenant <> a.region)")]
    public void GroupedAndOrPreservesLocalBranchesAndNullSafeOperators(string on)
    {
        var sql = Tables + "CREATE VIEW app.v AS SELECT a.a_note, b.b_note " +
            "FROM app.a a LEFT JOIN app.b b ON " + on + ";";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.True(view.Columns[1].Nullable);
        Assert.All(view.SourceBindings, x => Assert.NotEmpty(x.Columns));
    }

    [Fact]
    public void PureLocalOnAcrossBothSourcesIsProven()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b " +
            "ON a.tenant = a.region OR b.tenant = b.region;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Contains("tenant", view.SourceBindings[0].Columns);
        Assert.Contains("region", view.SourceBindings[0].Columns);
        Assert.Contains("tenant", view.SourceBindings[1].Columns);
        Assert.Contains("region", view.SourceBindings[1].Columns);
    }

    [Fact]
    public void EveryLocalOnColumnIsPhysicallyProtectedFromDrop()
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note " +
            "FROM app.a a JOIN app.b b ON a.id = b.id " +
            "AND a.tenant = a.region AND b.score > b.region;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        foreach (var pair in new[] { ("a", "tenant"), ("a", "region"), ("b", "score"), ("b", "region") })
        {
            var blocked = Analyze(sql + $"ALTER TABLE app.{pair.Item1} DROP COLUMN {pair.Item2};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
        }
        var unrelated = Analyze(sql + "ALTER TABLE app.a DROP COLUMN score;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, unrelated.Statements[4].Status);
    }

    [Fact]
    public void ThreeSourceUsingStageCanBindLocalAndCrossPredicates()
    {
        var sql = Tables + "CREATE VIEW app.v AS SELECT a.a_note, c.c_note " +
            "FROM app.a a FULL JOIN app.b b USING (id) " +
            "LEFT JOIN app.c c ON (id = c.parent_id AND a.tenant = a.region) " +
            "OR c.tenant IS DISTINCT FROM c.region;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "app.a", "app.b", "app.c" },
            view.Dependencies.Select(x => x.DisplayName));
        Assert.Contains("tenant", view.SourceBindings[0].Columns);
        Assert.Contains("region", view.SourceBindings[0].Columns);
        Assert.Contains("tenant", view.SourceBindings[2].Columns);
        foreach (var table in new[] { "a", "b" })
        {
            var drop = Analyze(sql + $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Statements[4].Status);
        }
    }

    [Fact]
    public void SelfJoinKeepsSameSideOnBoundToAliasInstances()
    {
        var sql = "CREATE TABLE app.employees(id INTEGER NOT NULL, manager_id INTEGER, team INTEGER, department INTEGER, name TEXT NOT NULL);" +
            "CREATE VIEW app.v AS SELECT e.name AS worker, m.name AS manager " +
            "FROM app.employees e LEFT JOIN app.employees m " +
            "ON e.manager_id = m.id AND e.team = e.department " +
            "AND m.department IS NOT DISTINCT FROM m.team;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { false, true }, view.Columns.Select(x => x.Nullable));
        Assert.Single(view.SourceBindings);
        foreach (var name in new[] { "manager_id", "id", "team", "department" })
            Assert.Contains(name, view.SourceBindings[0].Columns);
        foreach (var name in new[] { "team", "department" })
        {
            var drop = Analyze(sql + $"ALTER TABLE app.employees DROP COLUMN {name};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Statements[2].Status);
        }
    }

    [Fact]
    public void ReplaceViewRebindsDroppedSameSideConditionColumns()
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b " +
            "ON a.id = b.id AND a.tenant = a.region;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b " +
            "ON a.id = b.id AND b.tenant = b.region;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(sql +
            "ALTER TABLE app.a DROP COLUMN region;").Statements[5].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(sql +
            "ALTER TABLE app.b DROP COLUMN region;").Statements[5].Status);
    }

    [Theory]
    [InlineData("a.id = b.id AND a.tenant = a.a_note")]
    [InlineData("a.id = b.id AND b.score = a.unknown")]
    [InlineData("a.id = b.id AND b.score = c.parent_id")]
    [InlineData("a.id = b.id AND a.score = 5")]
    [InlineData("a.id = b.id AND a.missing = a.region")]
    [InlineData("a.id = b.id AND a.id = b.id AND a.tenant = b.tenant AND b.score = b.region AND a.region = a.tenant AND b.region = b.score AND a.id = a.id AND b.id = b.id AND a.tenant = a.region")]
    public void UnprovenLocalPredicatesAndNinthLeafRemainFailClosed(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON " + on + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectCannotInheritPostgresLocalOnProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT a.id FROM app.a a " +
            "JOIN app.b b ON a.id = b.id AND a.tenant = a.region;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenCatalogStaysUnproven()
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT a.id FROM app.a a " +
            "JOIN app.b b ON a.id = b.id AND a.tenant = a.region;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task LivePostgresLocalOnPredicatesFilterMatchesButPreserveLeftRows()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }
        var schema = "sqlcore_sameon_" + Guid.NewGuid().ToString("N");
        var sql =
            $"CREATE TABLE {schema}.a(id INTEGER NOT NULL, tenant INTEGER NOT NULL, region INTEGER, a_note TEXT NOT NULL);" +
            $"CREATE TABLE {schema}.b(id INTEGER NOT NULL, tenant INTEGER, region INTEGER, b_note TEXT NOT NULL);" +
            $"CREATE VIEW {schema}.v_and AS SELECT a.a_note,b.b_note FROM {schema}.a a LEFT JOIN {schema}.b b ON a.id = b.id AND a.tenant = a.region AND b.tenant > b.region;" +
            $"CREATE VIEW {schema}.v_or AS SELECT a.a_note,b.b_note FROM {schema}.a a LEFT JOIN {schema}.b b ON a.id = b.id AND (a.tenant = a.region OR b.tenant > b.region);";
        var analyzed = Analyze(sql);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Exec(string cmd)
        {
            await using var statement = new NpgsqlCommand(cmd, db, tx);
            await statement.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Exec("CREATE SCHEMA " + schema);
        foreach (var statement in analyzed.Statements) await Exec(statement.Syntax.Text);
        await Exec($"INSERT INTO {schema}.a VALUES (1,1,2,'a1'),(2,1,1,'a2'),(3,2,2,'a3');");
        await Exec($"INSERT INTO {schema}.b VALUES (1,10,5,'b1'),(2,1,0,'b2'),(3,5,10,'b3');");
        async Task<string[]> Rows(string name)
        {
            var result = new List<string>();
            await using var cmd = new NpgsqlCommand($"SELECT a_note,b_note FROM {schema}.{name} ORDER BY a_note", db, tx);
            await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await r.ReadAsync(TestContext.Current.CancellationToken))
                result.Add(r.GetString(0) + "|" + (r.IsDBNull(1) ? "-" : r.GetString(1)));
            return result.ToArray();
        }
        Assert.Equal(new[] { "a1|-", "a2|b2", "a3|-" }, await Rows("v_and"));
        Assert.Equal(new[] { "a1|b1", "a2|b2", "a3|b3" }, await Rows("v_or"));
        foreach (var table in new[] { "a", "b" })
        {
            await Exec("SAVEPOINT guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Exec($"ALTER TABLE {schema}.{table} DROP COLUMN region;"));
            await Exec("ROLLBACK TO SAVEPOINT guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
