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
/// PostgreSQL CREATE VIEW ON Boolean expression proof: typed equality
/// leaves, AND-before-OR precedence, nested grouping, NULL-safe equality,
/// and full physical lineage without executing arbitrary SQL.
/// </summary>
public sealed class SqlCorePostgresViewOnBooleanGraphProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER, ext_id INTEGER, tenant INTEGER NOT NULL, a_note TEXT);" +
        "CREATE TABLE app.b(id INTEGER, ext_id INTEGER, tenant INTEGER NOT NULL, b_note TEXT);" +
        "CREATE TABLE app.c(parent_id INTEGER, ext_id INTEGER, tenant INTEGER NOT NULL, c_note TEXT);" +
        "CREATE TABLE app.d(parent_id INTEGER, ext_id INTEGER, tenant INTEGER NOT NULL, d_note TEXT);";

    private static SqlScriptAnalysisResult Analyze(string script,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(script, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN")]
    [InlineData("INNER JOIN")]
    [InlineData("LEFT JOIN")]
    [InlineData("RIGHT OUTER JOIN")]
    [InlineData("FULL JOIN")]
    public void TwoSourceOrOfTypedCrossEdgeEqualitiesPublishesKnownView(string kind)
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note FROM app.a a " + kind +
            " app.b b ON a.id = b.id OR a.ext_id = b.ext_id;";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "a_note", "b_note" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "app.a", "app.b" }, view.Dependencies.Select(d => d!.DisplayName));
        foreach (var binding in view.SourceBindings)
        {
            Assert.Contains("id", binding.Columns);
            Assert.Contains("ext_id", binding.Columns);
        }
        foreach (var table in new[] { "a", "b" })
        foreach (var key in new[] { "id", "ext_id" })
        {
            var dropped = Analyze(script + $"ALTER TABLE app.{table} DROP COLUMN {key};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropped.Statements[5].Status);
            Assert.Empty(dropped.Statements[5].Changes);
        }
    }

    [Theory]
    [InlineData("a.id = b.id OR a.ext_id = b.ext_id AND a.tenant = b.tenant")]
    [InlineData("(a.id = b.id OR a.ext_id = b.ext_id) AND a.tenant = b.tenant")]
    [InlineData("((a.id = b.id) OR ((a.ext_id = b.ext_id) AND (a.tenant = b.tenant)))")]
    [InlineData("a.id IS NOT DISTINCT FROM b.id OR a.ext_id = b.ext_id")]
    [InlineData("(a.id IS NOT DISTINCT FROM b.id OR a.ext_id = b.ext_id) AND a.tenant = b.tenant")]
    [InlineData("((a.id = b.id OR a.ext_id = b.ext_id) AND (a.tenant IS NOT DISTINCT FROM b.tenant))")]
    public void GroupedOrAndNullSafeLeavesStayClosedAndTypeProven(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note FROM app.a a " +
            "LEFT JOIN app.b b ON " + on + ";");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.True(view.Columns[1].Nullable);
        Assert.Contains("id", view.SourceBindings[0].Columns);
        Assert.Contains("ext_id", view.SourceBindings[1].Columns);
    }

    [Theory]
    [InlineData("id = c.parent_id OR a.ext_id = c.ext_id")]
    [InlineData("(id IS NOT DISTINCT FROM c.parent_id OR a.ext_id = c.ext_id) AND a.tenant = c.tenant")]
    [InlineData("a.tenant = c.tenant AND (c.parent_id = id OR c.ext_id = b.ext_id)")]
    public void MultiUsingGraphOnOrPreservesMergedPhysicalKeyLineage(string on)
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, c.c_note " +
            "FROM app.a a FULL JOIN app.b b USING (id) " +
            "LEFT JOIN app.c c ON " + on + ";";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "app.a", "app.b", "app.c" },
            view.Dependencies.Select(x => x!.DisplayName));
        foreach (var table in new[] { "a", "b" })
        {
            var dropped = Analyze(script + $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropped.Statements[5].Status);
        }
        var rightDrop = Analyze(script + "ALTER TABLE app.c DROP COLUMN parent_id;");
        if (on.Contains("parent_id", StringComparison.Ordinal))
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, rightDrop.Statements[5].Status);
    }

    [Fact]
    public void FourSourceMixedJoinOrAndConservesEveryStageAndDependency()
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, c.c_note, d.d_note " +
            "FROM app.a a JOIN app.b b USING (id) " +
            "LEFT JOIN app.c c ON (id = c.parent_id OR a.ext_id = c.ext_id) " +
            "FULL JOIN app.d d ON (c.parent_id = d.parent_id OR b.ext_id = d.ext_id) AND a.tenant = d.tenant;";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(4, view.SourceBindings.Count);
        Assert.Equal(new[] { "a_note", "c_note", "d_note" }, view.Columns.Select(x => x.Name));
        Assert.True(view.Columns[2].Nullable);
        foreach (var binding in view.SourceBindings)
            Assert.NotEmpty(binding.Columns);
    }

    [Fact]
    public void EightMixedOrAndLeavesAllowedButNineAreRejected()
    {
        var cols = string.Join(",", Enumerable.Range(1, 9).Select(i => $"k{i} INTEGER"));
        var leaves = Enumerable.Range(1, 9).Select(i =>
            $"a.k{i} " + (i % 2 == 0 ? "IS NOT DISTINCT FROM" : "=") + $" b.k{i}").ToArray();
        var prefix = $"CREATE TABLE app.a({cols}, a_note TEXT);" +
            $"CREATE TABLE app.b({cols}, b_note TEXT);";
        var good = Analyze(prefix + "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON " +
            "(" + string.Join(" OR ", leaves.Take(4)) + ") AND (" +
            string.Join(" OR ", leaves.Skip(4).Take(4)) + ");");
        Assert.Empty(good.Diagnostics);
        var view = Assert.Single(good.FinalCatalog.Views);
        Assert.Equal(9, view.SourceBindings[0].Columns.Count);
        Assert.Equal(8, view.SourceBindings[1].Columns.Count);
        var bad = Analyze(prefix + "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON " +
            string.Join(" OR ", leaves) + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, bad.Statements[2].Status);
        Assert.Empty(bad.FinalCatalog.Views);
    }

    [Fact]
    public void OrViewReplacementRewiresProvenColumnBindings()
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON a.id = b.id;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.c c " +
            "ON a.id = c.parent_id OR a.ext_id = c.ext_id;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[5].Changes).Operation);
        Assert.Equal(new[] { "app.a", "app.c" },
            Assert.Single(result.FinalCatalog.Views).Dependencies.Select(x => x!.DisplayName));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed,
            Analyze(sql + "ALTER TABLE app.b DROP COLUMN id;").Statements[6].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid,
            Analyze(sql + "ALTER TABLE app.c DROP COLUMN ext_id;").Statements[6].Status);
    }

    [Theory]
    [InlineData("a.id = b.id OR a.id = a.ext_id")]
    [InlineData("a.id = b.id OR b.id = b.ext_id")]
    [InlineData("a.id = b.id OR a.id = b.b_note")]
    [InlineData("a.id = b.id OR a.id = missing.ext_id")]
    [InlineData("a.id = b.id OR a.id = c.parent_id")]
    [InlineData("a.id = b.id OR a.id = 1")]
    [InlineData("a.id = b.id OR TRUE")]
    [InlineData("a.id = b.id OR")]
    [InlineData("a.id = b.id XOR a.ext_id = b.ext_id")]
    [InlineData("a.id = b.id OR NOT (a.ext_id = b.ext_id)")]
    [InlineData("a.id = b.id OR a.id IS DISTINCT FROM b.id")]
    public void MalformedOrUnprovenOrGraphNeverPublishesKnownView(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON " + on + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.DoesNotContain(result.Statements[4].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("(a.id = b.id OR a.ext_id = b.ext_id")]
    [InlineData("a.id = b.id OR a.ext_id = b.ext_id)")]
    public void UnbalancedOnParenthesesAreRejectedByLexicalStage(string on)
    {
        Assert.Throws<HsSqlAgent.SqlCore.SqlParsing.SqlParseException>(() =>
            Analyze(Tables + "CREATE VIEW app.v AS SELECT a.a_note " +
                "FROM app.a a JOIN app.b b ON " + on + ";"));
    }

    [Fact]
    public void MoreThanSixteenNestedOnGroupsFailsClosed()
    {
        var body = "a.id = b.id OR a.ext_id = b.ext_id";
        var deep = new string('(', 18) + body + new string(')', 18);
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON " + deep + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherFiveDialectsCannotClaimPostgresOrJoinProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b " +
            "ON a.id = b.id OR a.ext_id = b.ext_id;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenCatalogCannotClaimOrJoinProof()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b " +
            "ON a.id = b.id OR a.ext_id = b.ext_id;", SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task LivePostgresAndOrPrecedenceAndNullSafeOrMatchRealRows()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }
        var schema = "sqlcore_onor_" + Guid.NewGuid().ToString("N");
        var sql =
            $"CREATE TABLE {schema}.a(id INTEGER, ext_id INTEGER, tenant INTEGER NOT NULL, a_note TEXT);" +
            $"CREATE TABLE {schema}.b(id INTEGER, ext_id INTEGER, tenant INTEGER NOT NULL, b_note TEXT);" +
            $"CREATE VIEW {schema}.v_precedence AS SELECT a.a_note, b.b_note FROM {schema}.a a JOIN {schema}.b b ON a.id = b.id OR a.ext_id = b.ext_id AND a.tenant = b.tenant;" +
            $"CREATE VIEW {schema}.v_grouped AS SELECT a.a_note, b.b_note FROM {schema}.a a JOIN {schema}.b b ON (a.id = b.id OR a.ext_id = b.ext_id) AND a.tenant = b.tenant;" +
            $"CREATE VIEW {schema}.v_nullsafe AS SELECT a.a_note, b.b_note FROM {schema}.a a JOIN {schema}.b b ON (a.id IS NOT DISTINCT FROM b.id OR a.ext_id = b.ext_id) AND a.tenant = b.tenant;";
        var analyzed = Analyze(sql);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements,
            s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
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
        await Execute($"INSERT INTO {schema}.a VALUES (1,11,1,'a1'),(2,22,1,'a2'),(NULL,33,1,'a3');");
        await Execute($"INSERT INTO {schema}.b VALUES (1,99,2,'b1'),(9,22,1,'b2'),(NULL,44,1,'b3');");
        async Task<string[]> Rows(string view)
        {
            var rows = new List<string>();
            await using var cmd = new NpgsqlCommand(
                $"SELECT a_note, b_note FROM {schema}.{view} ORDER BY a_note, b_note", db, tx);
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(reader.GetString(0) + "|" + reader.GetString(1));
            return rows.ToArray();
        }
        Assert.Equal(new[] { "a1|b1", "a2|b2" }, await Rows("v_precedence"));
        Assert.Equal(new[] { "a2|b2" }, await Rows("v_grouped"));
        Assert.Equal(new[] { "a2|b2", "a3|b3" }, await Rows("v_nullsafe"));
        foreach (var table in new[] { "a", "b" })
        {
            await Execute("SAVEPOINT or_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Execute($"ALTER TABLE {schema}.{table} DROP COLUMN id;"));
            await Execute("ROLLBACK TO SAVEPOINT or_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
