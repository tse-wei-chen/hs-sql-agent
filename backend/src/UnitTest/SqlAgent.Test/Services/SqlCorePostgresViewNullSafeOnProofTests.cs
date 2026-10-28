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
/// PostgreSQL bounded CREATE VIEW ON equality supports both ordinary =
/// and NULL-safe IS NOT DISTINCT FROM as distinct, closed operators.
/// </summary>
public sealed class SqlCorePostgresViewNullSafeOnProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER, tenant INTEGER NOT NULL, a_note TEXT);" +
        "CREATE TABLE app.b(id INTEGER, tenant INTEGER NOT NULL, b_note TEXT);" +
        "CREATE TABLE app.c(parent_id INTEGER, tenant INTEGER NOT NULL, c_note TEXT);";

    private static SqlScriptAnalysisResult Analyze(string script,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(script, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN")]
    [InlineData("INNER JOIN")]
    [InlineData("LEFT JOIN")]
    [InlineData("LEFT OUTER JOIN")]
    [InlineData("RIGHT JOIN")]
    [InlineData("FULL OUTER JOIN")]
    public void TwoSourceNullSafeOnJoinsAreStaticallyProven(string kind)
    {
        var analyzed = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.id AS left_id, b.id AS right_id " +
            "FROM app.a a " + kind + " app.b b ON a.id IS NOT DISTINCT FROM b.id;");
        Assert.Empty(analyzed.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analyzed.Statements[3].Status);
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Equal(new[] { "left_id", "right_id" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { true, true }, view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "app.a", "app.b" }, view.Dependencies.Select(d => d!.DisplayName));
        foreach (var table in new[] { "a", "b" })
        {
            var dropped = Analyze(Tables +
                "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a " + kind +
                " app.b b ON a.id IS NOT DISTINCT FROM b.id;" +
                $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropped.Statements[4].Status);
            Assert.Empty(dropped.Statements[4].Changes);
        }
    }

    [Theory]
    [InlineData("a.id = b.id AND a.tenant IS NOT DISTINCT FROM b.tenant")]
    [InlineData("((a.id IS NOT DISTINCT FROM b.id) AND (a.tenant = b.tenant))")]
    [InlineData("(a.tenant = b.tenant) AND ((a.id IS NOT DISTINCT FROM b.id))")]
    [InlineData("a.id IS NOT DISTINCT FROM b.id AND a.tenant IS NOT DISTINCT FROM b.tenant")]
    public void NullSafeAndRegularEqualityCanShareBoundedCompoundOn(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note " +
            "FROM app.a a LEFT JOIN app.b b ON " + on + ";");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.True(view.Columns[1].Nullable);
        Assert.Contains("id", view.SourceBindings[0].Columns);
        Assert.Contains("id", view.SourceBindings[1].Columns);
        Assert.Contains("tenant", view.SourceBindings[0].Columns);
        Assert.Contains("tenant", view.SourceBindings[1].Columns);
    }

    [Theory]
    [InlineData("id IS NOT DISTINCT FROM c.parent_id")]
    [InlineData("c.parent_id IS NOT DISTINCT FROM id")]
    [InlineData("(id IS NOT DISTINCT FROM c.parent_id) AND a.tenant = c.tenant")]
    [InlineData("a.tenant = c.tenant AND (c.parent_id IS NOT DISTINCT FROM id)")]
    public void StageMergedUsingKeyCanBindNullSafeOnWithCompletePhysicalLineage(string on)
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, c.c_note FROM app.a a " +
            "FULL JOIN app.b b USING (id) LEFT JOIN app.c c ON " + on + ";";
        var analyzed = Analyze(script);
        Assert.Empty(analyzed.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analyzed.Statements[3].Status);
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Equal(new[] { "a_note", "c_note" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { true, true }, view.Columns.Select(c => c.Nullable));
        Assert.All(view.SourceBindings, binding => Assert.NotEmpty(binding.Columns));
        foreach (var table in new[] { "a", "b" })
        {
            var drop = Analyze(script + $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Statements[4].Status);
        }
        var dropRight = Analyze(script + "ALTER TABLE app.c DROP COLUMN parent_id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropRight.Statements[4].Status);
    }

    [Fact]
    public void EightMixedNullSafeOnPredicatesStayInsideBoundedGrammar()
    {
        var cols = string.Join(",", Enumerable.Range(1, 8).Select(i => $"k{i} INTEGER"));
        var on = string.Join(" AND ", Enumerable.Range(1, 8).Select(i =>
            $"a.k{i} " + (i % 2 == 0 ? "IS NOT DISTINCT FROM" : "=") + $" b.k{i}"));
        var result = Analyze(
            $"CREATE TABLE app.a({cols}, a_note TEXT);" +
            $"CREATE TABLE app.b({cols}, b_note TEXT);" +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note FROM app.a a " +
            "JOIN app.b b ON " + on + ";");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(9, view.SourceBindings[0].Columns.Count);
        Assert.Equal(9, view.SourceBindings[1].Columns.Count);
    }

    [Fact]
    public void NullSafeViewReplacementRebindsBothJoinDependencies()
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note AS note FROM app.a a " +
            "JOIN app.b b ON a.id = b.id;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT a.a_note AS note FROM app.a a " +
            "JOIN app.c c ON a.id IS NOT DISTINCT FROM c.parent_id;";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "app.a", "app.c" }, view.Dependencies.Select(d => d!.DisplayName));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(script +
            "ALTER TABLE app.b DROP COLUMN id;").Statements[5].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(script +
            "ALTER TABLE app.c DROP COLUMN parent_id;").Statements[5].Status);
    }

    [Theory]
    [InlineData("a.id IS DISTINCT FROM a.tenant")]
    [InlineData("a.id IS NOT DISTINCT b.id")]
    [InlineData("a.id IS DISTINCT b.id")]
    [InlineData("a.id IS NOT NULL")]
    [InlineData("a.id <> a.tenant")]
    [InlineData("a.id IS NOT DISTINCT FROM 1")]
    [InlineData("a.id IS NOT DISTINCT FROM b.id OR a.id = a.tenant")]
    [InlineData("a.id IS NOT DISTINCT FROM a.tenant")]
    [InlineData("b.id IS NOT DISTINCT FROM b.tenant")]
    [InlineData("a.a_note IS NOT DISTINCT FROM b.id")]
    [InlineData("a.id IS NOT DISTINCT FROM c.parent_id")]
    public void UnsafeNullSafeOnGrammarNeverMintsKnownView(string predicate)
    {
        var analyzed = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON " +
            predicate + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, analyzed.Statements[3].Status);
        Assert.DoesNotContain(analyzed.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(analyzed.FinalCatalog.Views);
    }

    [Fact]
    public void NinthMixedNullSafeOnPredicateFailsClosed()
    {
        var cols = string.Join(",", Enumerable.Range(1, 9).Select(i => $"k{i} INTEGER"));
        var on = string.Join(" AND ", Enumerable.Range(1, 9).Select(i =>
            $"a.k{i} IS NOT DISTINCT FROM b.k{i}"));
        var analyzed = Analyze(
            $"CREATE TABLE app.a({cols});" +
            $"CREATE TABLE app.b({cols});" +
            "CREATE VIEW app.v AS SELECT a.k1 FROM app.a a JOIN app.b b ON " + on + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, analyzed.Statements[2].Status);
        Assert.Empty(analyzed.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherFiveDialectsNeverInheritPostgresNullSafeOnProof(SqlAgentToolType dialect)
    {
        var analyzed = Analyze(
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b " +
            "ON a.id IS NOT DISTINCT FROM b.id;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(analyzed.Statements).Status);
        Assert.Empty(analyzed.FinalCatalog.Views);
    }

    [Fact]
    public void OpenCatalogNeverClaimsNullSafeOnProvenance()
    {
        var analyzed = Analyze(
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b " +
            "ON a.id IS NOT DISTINCT FROM b.id;", SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(analyzed.Statements).Status);
        Assert.False(analyzed.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task LivePostgresNullSafeOnDiffersFromEqualsForTwoNullKeys()
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
        var schema = "sqlcore_nullsafe_on_" + Guid.NewGuid().ToString("N");
        var setup =
            $"CREATE TABLE {schema}.a(id INTEGER, a_note TEXT);" +
            $"CREATE TABLE {schema}.b(id INTEGER, b_note TEXT);" +
            $"CREATE VIEW {schema}.v_null AS SELECT a.id AS left_id, b.id AS right_id FROM {schema}.a a JOIN {schema}.b b ON a.id IS NOT DISTINCT FROM b.id;" +
            $"CREATE VIEW {schema}.v_equal AS SELECT a.id AS left_id, b.id AS right_id FROM {schema}.a a JOIN {schema}.b b ON a.id = b.id;";
        var analyzed = Analyze(setup);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Exec(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Exec("CREATE SCHEMA " + schema);
        foreach (var stmt in analyzed.Statements)
            await Exec(stmt.Syntax.Text);
        await Exec($"INSERT INTO {schema}.a VALUES (1,'left-1'), (2,'left-2'),(NULL,'left-null');");
        await Exec($"INSERT INTO {schema}.b VALUES (1,'right-1'), (3,'right-3'),(NULL,'right-null');");
        async Task<long> Count(string view, bool onlyNull)
        {
            var where = onlyNull ? " WHERE left_id IS NULL AND right_id IS NULL" : "";
            await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {schema}.{view}" + where, db, tx);
            return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
        Assert.Equal(2L, await Count("v_null", false));
        Assert.Equal(1L, await Count("v_null", true));
        Assert.Equal(1L, await Count("v_equal", false));
        Assert.Equal(0L, await Count("v_equal", true));
        foreach (var t in new[] { "a", "b" })
        {
            await Exec("SAVEPOINT nullsafe_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Exec($"ALTER TABLE {schema}.{t} DROP COLUMN id;"));
            await Exec("ROLLBACK TO SAVEPOINT nullsafe_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
