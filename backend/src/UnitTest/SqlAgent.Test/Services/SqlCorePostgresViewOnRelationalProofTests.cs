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

/// <summary>PostgreSQL CREATE VIEW JOIN ON: typed relational predicates
/// are distinct closed AST operators, never lowered to equality.</summary>
public sealed class SqlCorePostgresViewOnRelationalProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER, amount INTEGER, tag TEXT, a_note TEXT NOT NULL);" +
        "CREATE TABLE app.b(id INTEGER, amount INTEGER, tag TEXT, b_note TEXT NOT NULL);" +
        "CREATE TABLE app.c(parent_id INTEGER, amount INTEGER, c_note TEXT);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, state ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("<>")]
    [InlineData("!=")]
    [InlineData("IS DISTINCT FROM")]
    public void IntegerColumnComparisonsAreProvenWithoutTreatingThemAsEquality(string op)
    {
        var sql = Tables + "CREATE VIEW app.v AS SELECT a.a_note, b.b_note " +
            "FROM app.a a LEFT JOIN app.b b ON a.id " + op + " b.id;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "a_note", "b_note" }, view.Columns.Select(x => x.Name));
        Assert.True(view.Columns[1].Nullable);
        Assert.Contains("id", view.SourceBindings[0].Columns);
        Assert.Contains("id", view.SourceBindings[1].Columns);
        foreach (var table in new[] { "a", "b" })
        {
            var dropped = Analyze(sql + $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropped.Statements[4].Status);
            Assert.Empty(dropped.Statements[4].Changes);
        }
    }

    [Theory]
    [InlineData("JOIN")]
    [InlineData("RIGHT JOIN")]
    [InlineData("FULL OUTER JOIN")]
    public void NonEqualityJoinKindPreservesNullExtension(string kind)
    {
        var analyzed = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note FROM app.a a " +
            kind + " app.b b ON a.amount >= b.amount;");
        Assert.Empty(analyzed.Diagnostics);
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Equal(new[] { kind == "FULL OUTER JOIN" || kind == "RIGHT JOIN",
                             kind == "FULL OUTER JOIN" }, view.Columns.Select(x => x.Nullable));
    }

    [Theory]
    [InlineData("a.id < b.id OR a.amount <> b.amount")]
    [InlineData("(a.id IS DISTINCT FROM b.id OR a.tag = b.tag) AND a.amount >= b.amount")]
    [InlineData("a.tag < b.tag AND a.id != b.id OR a.amount <= b.amount")]
    [InlineData("a.id IS NOT DISTINCT FROM b.id OR a.amount > b.amount")]
    [InlineData("((a.id != b.id) AND (a.amount <= b.amount OR a.tag > b.tag))")]
    public void NonEqualityAndEqualityMixWithBoundedAndOrAndTextOrdering(string on)
    {
        var analyzed = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, b.b_note FROM app.a a JOIN app.b b ON " + on + ";");
        Assert.Empty(analyzed.Diagnostics);
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Contains("id", view.SourceBindings[0].Columns);
        Assert.Contains("amount", view.SourceBindings[1].Columns);
    }

    [Theory]
    [InlineData("id > c.parent_id")]
    [InlineData("c.parent_id < id")]
    [InlineData("(id IS DISTINCT FROM c.parent_id OR a.amount >= c.amount)")]
    [InlineData("(id IS NOT DISTINCT FROM c.parent_id OR b.amount <> c.amount)")]
    public void MultiUsingGraphKeepsBothKeysWhenOnUsesRelationalOperator(string on)
    {
        var script = Tables + "CREATE VIEW app.v AS SELECT a.a_note, c.c_note " +
            "FROM app.a a FULL JOIN app.b b USING (id) LEFT JOIN app.c c ON " + on + ";";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "app.a", "app.b", "app.c" },
            view.Dependencies.Select(x => x!.DisplayName));
        foreach (var table in new[] { "a", "b" })
        {
            var drop = Analyze(script + $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Statements[4].Status);
        }
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(script +
            "ALTER TABLE app.c DROP COLUMN parent_id;").Statements[4].Status);
    }

    [Fact]
    public void ViewReplacementRebindsRelationalJoinSources()
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON a.id = b.id;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.c c " +
            "ON a.id < c.parent_id OR a.amount IS DISTINCT FROM c.amount;";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "app.a", "app.c" }, view.Dependencies.Select(x => x!.DisplayName));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(script +
            "ALTER TABLE app.b DROP COLUMN id;").Statements[5].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(script +
            "ALTER TABLE app.c DROP COLUMN parent_id;").Statements[5].Status);
    }

    [Theory]
    [InlineData("a.id < b.tag")]
    [InlineData("a.tag >= b.id")]
    [InlineData("a.id IS DISTINCT FROM b.tag")]
    [InlineData("a.id < a.amount")]
    [InlineData("b.id <> b.amount")]
    [InlineData("a.id >= 1")]
    [InlineData("a.id LIKE b.tag")]
    [InlineData("a.id BETWEEN b.id AND b.amount")]
    [InlineData("a.id IS DISTINCT b.id")]
    [InlineData("a.id IS NOT DISTINCT b.id")]
    [InlineData("a.id < c.parent_id")]
    [InlineData("a.id > b.id OR a.tag >= b.id")]
    [InlineData("a.id > b.id OR b.id > b.tag")]
    public void InvalidRelationalOperatorsTypesAndOperandsFailClosed(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b ON " + on + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherFiveDialectsNeverInheritPostgresRelationalOnProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT a.a_note FROM app.a a JOIN app.b b " +
            "ON a.id IS DISTINCT FROM b.id;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenWorldCatalogRemainsUnproven()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b ON a.id < b.id;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task LivePostgresDistinctFromAndOrderedJoinMatchActualRows()
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
        var schema = "sqlcore_onrel_" + Guid.NewGuid().ToString("N");
        var setup =
            $"CREATE TABLE {schema}.a(id INTEGER, amount INTEGER, a_note TEXT);" +
            $"CREATE TABLE {schema}.b(id INTEGER, amount INTEGER, b_note TEXT);" +
            $"CREATE VIEW {schema}.v_less AS SELECT a.a_note,b.b_note FROM {schema}.a a JOIN {schema}.b b ON a.id < b.id;" +
            $"CREATE VIEW {schema}.v_notequal AS SELECT a.a_note,b.b_note FROM {schema}.a a JOIN {schema}.b b ON a.id <> b.id;" +
            $"CREATE VIEW {schema}.v_distinct AS SELECT a.a_note,b.b_note FROM {schema}.a a JOIN {schema}.b b ON a.id IS DISTINCT FROM b.id;" +
            $"CREATE VIEW {schema}.v_mixed AS SELECT a.a_note,b.b_note FROM {schema}.a a JOIN {schema}.b b ON (a.id = b.id OR a.amount > b.amount) AND a.id IS DISTINCT FROM b.id;";
        var analysis = Analyze(setup);
        Assert.Empty(analysis.Diagnostics);
        Assert.All(analysis.Statements,
            s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Exec(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Exec("CREATE SCHEMA " + schema);
        foreach (var statement in analysis.Statements)
            await Exec(statement.Syntax.Text);
        await Exec($"INSERT INTO {schema}.a VALUES (1,10,'a1'),(2,20,'a2'),(NULL,30,'anull');");
        await Exec($"INSERT INTO {schema}.b VALUES (2,15,'b2'),(3,25,'b3'),(NULL,30,'bnull');");
        async Task<string[]> Rows(string view)
        {
            var rows = new List<string>();
            await using var cmd = new NpgsqlCommand(
                $"SELECT a_note,b_note FROM {schema}.{view} ORDER BY a_note,b_note", db, tx);
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(reader.GetString(0) + "|" + reader.GetString(1));
            return rows.ToArray();
        }
        Assert.Equal(new[] { "a1|b2", "a1|b3", "a2|b3" }, await Rows("v_less"));
        Assert.Equal(new[] { "a1|b2", "a1|b3", "a2|b3" }, await Rows("v_notequal"));
        Assert.Equal(7, (await Rows("v_distinct")).Length);
        Assert.DoesNotContain("anull|bnull", await Rows("v_distinct"));
        Assert.Equal(new[] { "anull|b2", "anull|b3" }, await Rows("v_mixed"));
        foreach (var t in new[] { "a", "b" })
        {
            await Exec("SAVEPOINT rel_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Exec($"ALTER TABLE {schema}.{t} DROP COLUMN id;"));
            await Exec("ROLLBACK TO SAVEPOINT rel_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
