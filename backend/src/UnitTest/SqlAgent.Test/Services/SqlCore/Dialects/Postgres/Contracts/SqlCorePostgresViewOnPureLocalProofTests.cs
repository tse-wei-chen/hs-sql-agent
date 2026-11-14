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
/// PostgreSQL permits an ON expression with no cross-edge comparison.
/// The proof must still bind every typed leaf at the correct stage, retain
/// all physical dependencies, and never push ON predicates into WHERE.
/// </summary>
public sealed class SqlCorePostgresViewOnPureLocalProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER NOT NULL, tenant INTEGER, region INTEGER, a_note TEXT NOT NULL);" +
        "CREATE TABLE app.b(id INTEGER NOT NULL, tenant INTEGER, region INTEGER, b_note TEXT NOT NULL);" +
        "CREATE TABLE app.c(parent_id INTEGER NOT NULL, tenant INTEGER, region INTEGER, c_note TEXT NOT NULL);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN", "a.tenant = a.region", false, false)]
    [InlineData("LEFT JOIN", "a.tenant = a.region", false, true)]
    [InlineData("RIGHT JOIN", "a.tenant = a.region", true, false)]
    [InlineData("INNER JOIN", "b.tenant = b.region", false, false)]
    [InlineData("LEFT JOIN", "b.tenant = b.region", false, true)]
    [InlineData("RIGHT OUTER JOIN", "b.tenant = b.region", true, false)]
    public void LocalOnlyOnBindsAndKeepsOuterNullExtension(
        string kind, string on, bool leftNullable, bool rightNullable)
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT a.id AS left_id, b.id AS right_id " +
            "FROM app.a a " + kind + " app.b b ON " + on + ";";
        var analyzed = Analyze(sql);
        Assert.Empty(analyzed.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analyzed.Statements[3].Status);
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Equal(new[] { "left_id", "right_id" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { leftNullable, rightNullable }, view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "app.a", "app.b" }, view.Dependencies.Select(d => d.DisplayName));
        var local = on.StartsWith("a.", StringComparison.Ordinal) ? 0 : 1;
        Assert.Contains("tenant", view.SourceBindings[local].Columns);
        Assert.Contains("region", view.SourceBindings[local].Columns);
        var owner = local == 0 ? "a" : "b";
        foreach (var column in new[] { "tenant", "region" })
        {
            var blocked = Analyze(sql + $"ALTER TABLE app.{owner} DROP COLUMN {column};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
        }
    }

    /// PostgreSQL's FULL JOIN planner needs a mandatory cross-source
    /// hashable/mergeable equality, not a local-only or disjunctive ON.
    [Theory]
    [InlineData("a.tenant = a.region")]
    [InlineData("b.tenant = b.region")]
    [InlineData("a.tenant < a.region")]
    [InlineData("a.id = b.id OR a.tenant = a.region")]
    [InlineData("a.id IS NOT DISTINCT FROM b.id")]
    [InlineData("a.id < b.id")]
    public void FullJoinWithoutMandatoryCrossEqualityFailsClosed(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a FULL JOIN app.b b ON " + on + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("(a.tenant = a.region OR b.tenant = b.region)")]
    [InlineData("a.tenant IS NOT DISTINCT FROM a.region AND b.tenant <> b.region")]
    [InlineData("((a.tenant = a.region) OR (b.tenant IS DISTINCT FROM b.region))")]
    [InlineData("(a.tenant < a.region OR b.tenant >= b.region) AND b.id > b.region")]
    [InlineData("(a.id = a.id) AND ((b.tenant > b.region) OR (a.region IS DISTINCT FROM a.tenant))")]
    public void AllLocalAndOrBranchesRetainTypeAndPhysicalLineage(string on)
    {
        var analyzed = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note " +
            "FROM app.a a LEFT JOIN app.b b ON " + on + ";");
        Assert.Empty(analyzed.Diagnostics);
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.True(view.Columns[1].Nullable);
        if (on.Contains("a.tenant", StringComparison.Ordinal))
            Assert.Contains("tenant", view.SourceBindings[0].Columns);
        if (on.Contains("b.region", StringComparison.Ordinal))
            Assert.Contains("region", view.SourceBindings[1].Columns);
    }

    [Fact]
    public void ThirdSourceLocalOnlyEdgeAfterUsingCanResolveMergedVisibleKey()
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT id AS merged_id, c.c_note " +
            "FROM app.a a FULL JOIN app.b b USING (id) " +
            "LEFT JOIN app.c c ON (a.tenant = a.region OR c.tenant = c.region);";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "merged_id", "c_note" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "app.a", "app.b", "app.c" },
            view.Dependencies.Select(d => d.DisplayName));
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
    public void FourthSourceCanUsePreviouslyMergedKeyInLocalOnlyOn()
    {
        var sql = Tables +
            "CREATE TABLE app.d(id INTEGER NOT NULL, d_note TEXT NOT NULL);" +
            "CREATE VIEW app.v AS SELECT id AS joined_id, d.d_note " +
            "FROM app.a a JOIN app.b b USING (id) " +
            "JOIN app.c c ON id = c.parent_id " +
            "LEFT JOIN app.d d ON a.tenant IS NOT DISTINCT FROM a.region;";
        var result = Analyze(sql);
        // The prior c input contributes no id column, so the merged id
        // remains unique; the new d input has id and makes SELECT id ambiguous.
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.FinalCatalog.Views);

        var qualified = Analyze(sql.Replace("SELECT id AS joined_id", "SELECT a.id AS joined_id",
            StringComparison.Ordinal));
        Assert.Empty(qualified.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, qualified.Statements[4].Status);
        Assert.Equal(4, Assert.Single(qualified.FinalCatalog.Views).SourceBindings.Count);
    }

    [Fact]
    public void SelfJoinLocalOnlyOnUsesTwoAliasInstancesOfOnePhysicalTable()
    {
        var sql =
            "CREATE TABLE app.employees(id INTEGER NOT NULL, team INTEGER, department INTEGER, name TEXT NOT NULL);" +
            "CREATE VIEW app.v AS SELECT e.name AS employee, m.name AS manager " +
            "FROM app.employees e LEFT JOIN app.employees m " +
            "ON e.team = e.department OR m.department IS NOT DISTINCT FROM m.team;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { false, true }, view.Columns.Select(c => c.Nullable));
        Assert.Single(view.Dependencies);
        Assert.Single(view.SourceBindings);
        foreach (var col in new[] { "team", "department" })
        {
            Assert.Contains(col, view.SourceBindings[0].Columns);
            var dropped = Analyze(sql + $"ALTER TABLE app.employees DROP COLUMN {col};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropped.Statements[2].Status);
        }
    }

    [Fact]
    public void ReplaceViewRewiresLocalOnlyOnPhysicalDependencies()
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b ON a.tenant = a.region;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b ON b.tenant = b.region;";
        var analyzed = Analyze(script);
        Assert.Empty(analyzed.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(analyzed.Statements[4].Changes).Operation);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(script +
            "ALTER TABLE app.a DROP COLUMN region;").Statements[5].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(script +
            "ALTER TABLE app.b DROP COLUMN region;").Statements[5].Status);
    }

    [Theory]
    [InlineData("a.tenant = a.a_note")]
    [InlineData("b.tenant >= b.b_note")]
    [InlineData("a.missing = a.region")]
    [InlineData("c.tenant = c.region")]
    [InlineData("a.id = 1")]
    [InlineData("a.tenant = b.id AND b.region = b.b_note")]
    [InlineData("a.id = a.id AND a.id = a.id AND a.id = a.id AND a.id = a.id AND a.id = a.id AND a.id = a.id AND a.id = a.id AND a.id = a.id AND a.id = a.id")]
    public void LocalOnlyMustStillFailClosedOnBadNamesTypesExpressionsAndBounds(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b ON " + on + ";");
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
    public void OtherFiveDialectsDoNotInheritPostgresLocalOnlyViewProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT a.id FROM app.a a " +
            "LEFT JOIN app.b b ON a.tenant = a.region;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnknownCatalogDoesNotPublishLocalOnlyProof()
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT a.id FROM app.a a " +
            "LEFT JOIN app.b b ON a.tenant = a.region;", SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task LivePostgresLocalOnlyOnPreservesOuterRowsAndNullThreeValuedLogic()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required for parity.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }

        var schema = "sqlcore_localonly_" + Guid.NewGuid().ToString("N");
        var sql =
            $"CREATE TABLE {schema}.a(id INTEGER NOT NULL, tenant INTEGER, region INTEGER, a_note TEXT NOT NULL);" +
            $"CREATE TABLE {schema}.b(id INTEGER NOT NULL, tenant INTEGER, region INTEGER, b_note TEXT NOT NULL);" +
            $"CREATE VIEW {schema}.v_left AS SELECT a.a_note, b.b_note FROM {schema}.a a LEFT JOIN {schema}.b b ON a.tenant = a.region;" +
            $"CREATE VIEW {schema}.v_right AS SELECT a.a_note, b.b_note FROM {schema}.a a LEFT JOIN {schema}.b b ON b.tenant = b.region;" +
            $"CREATE VIEW {schema}.v_nullsafe AS SELECT a.a_note, b.b_note FROM {schema}.a a LEFT JOIN {schema}.b b ON a.tenant IS NOT DISTINCT FROM a.region;";
        var analyzed = Analyze(sql);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Execute(string statement)
        {
            await using var cmd = new NpgsqlCommand(statement, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Execute("CREATE SCHEMA " + schema);
        foreach (var statement in analyzed.Statements)
            await Execute(statement.Syntax.Text);
        await Execute($"INSERT INTO {schema}.a VALUES (1,1,1,'a1'),(2,1,0,'a2'),(3,NULL,NULL,'a3');");
        await Execute($"INSERT INTO {schema}.b VALUES (10,2,2,'b1'),(20,2,0,'b2');");

        async Task<string[]> Rows(string view)
        {
            var items = new List<string>();
            await using var cmd = new NpgsqlCommand(
                $"SELECT a_note,b_note FROM {schema}.{view} ORDER BY COALESCE(a_note,'~'),COALESCE(b_note,'~')", db, tx);
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                items.Add((reader.IsDBNull(0) ? "-" : reader.GetString(0)) + "|" +
                    (reader.IsDBNull(1) ? "-" : reader.GetString(1)));
            return items.ToArray();
        }
        Assert.Equal(new[] { "a1|b1", "a1|b2", "a2|-", "a3|-" }, await Rows("v_left"));
        Assert.Equal(new[] { "a1|b1", "a2|b1", "a3|b1" }, await Rows("v_right"));
        Assert.Equal(new[] { "a1|b1", "a1|b2", "a2|-", "a3|b1", "a3|b2" }, await Rows("v_nullsafe"));
        // PostgreSQL accepts SQL syntax but rejects planning a FULL JOIN
        // without a cross-source equijoin. Check its actual SQLSTATE.
        await Execute("SAVEPOINT rejected_full");
        await using (var full = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {schema}.a a FULL JOIN {schema}.b b ON a.tenant < a.region", db, tx))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                full.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal("0A000", error.SqlState);
        }
        await Execute("ROLLBACK TO SAVEPOINT rejected_full");

        foreach (var table in new[] { "a", "b" })
        {
            await Execute("SAVEPOINT localonly_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Execute($"ALTER TABLE {schema}.{table} DROP COLUMN tenant;"));
            await Execute("ROLLBACK TO SAVEPOINT localonly_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
