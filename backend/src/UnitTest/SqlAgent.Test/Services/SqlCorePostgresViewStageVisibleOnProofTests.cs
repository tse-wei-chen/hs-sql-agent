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
/// PostgreSQL left-deep multi-USING / ON: unqualified ON operands must bind
/// against exactly one stage-visible column, never raw underlying duplicates.
/// Merged keys retain all physical dependencies even across FULL joins.
/// </summary>
public sealed class SqlCorePostgresViewStageVisibleOnProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER NOT NULL, tenant INTEGER NOT NULL, a_note TEXT);" +
        "CREATE TABLE app.b(id INTEGER NOT NULL, tenant INTEGER NOT NULL, b_note TEXT);" +
        "CREATE TABLE app.c(parent_id INTEGER NOT NULL, tenant_ref INTEGER NOT NULL, c_note TEXT);" +
        "CREATE TABLE app.d(parent_id INTEGER NOT NULL, d_note TEXT);";

    private static SqlScriptAnalysisResult Analyze(string script,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(script, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN")]
    [InlineData("LEFT JOIN")]
    [InlineData("RIGHT JOIN")]
    [InlineData("FULL JOIN")]
    public void UnqualifiedMergedLeftKeyCanBindOnWhenNewRightDoesNotExposeSameName(string join)
    {
        var sql = Tables +
            "CREATE VIEW app.v AS SELECT id AS public_id, c.c_note " +
            "FROM app.a a JOIN app.b b USING (id) " + join +
            " app.c c ON id = c.parent_id;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "c_note" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "app.a", "app.b", "app.c" },
            view.Dependencies.Select(d => d!.DisplayName));
        Assert.All(view.SourceBindings, x =>
            Assert.Contains(x.Relation.DisplayName == "app.c" ? "parent_id" : "id", x.Columns));
        Assert.Equal(join is "RIGHT JOIN" or "FULL JOIN", view.Columns[0].Nullable);
    }

    [Theory]
    [InlineData("ON id = c.parent_id")]
    [InlineData("ON c.parent_id = id")]
    [InlineData("ON id = parent_id")]
    [InlineData("ON parent_id = id")]
    public void SymmetricAndUnqualifiedNewRightOperandBindOnlyToItsStageOwner(string clause)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, c.c_note " +
            "FROM app.a a JOIN app.b b USING (id) " +
            "LEFT JOIN app.c c " + clause + ";");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "a_note", "c_note" }, view.Columns.Select(c => c.Name));
        Assert.True(view.Columns[1].Nullable);
        Assert.Contains("id", view.SourceBindings[0].Columns);
        Assert.Contains("id", view.SourceBindings[1].Columns);
        Assert.Contains("parent_id", view.SourceBindings[2].Columns);
    }

    [Fact]
    public void FullUsingCoalescedKeyOnNewRightInputTracksBothOriginalKeyOwners()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT id AS joined_id, c.c_note " +
            "FROM app.a a FULL JOIN app.b b USING (id) " +
            "LEFT JOIN app.c c ON id = parent_id;";
        var analyzed = Analyze(setup);
        Assert.Empty(analyzed.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analyzed.Statements[4].Status);
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Equal(new[] { "joined_id", "c_note" }, view.Columns.Select(c => c.Name));
        foreach (var table in new[] { "a", "b" })
        {
            var blocked = Analyze(setup + $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[5].Status);
            Assert.Empty(blocked.Statements[5].Changes);
        }
        var allowed = Analyze(setup + "ALTER TABLE app.b DROP COLUMN b_note;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[5].Status);
    }

    [Fact]
    public void FourSourceCompoundOnAfterTwoUsingStagesUsesLastMergedKey()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id, c.c_note, d.d_note " +
            "FROM app.a a JOIN app.b b USING (id,tenant) " +
            "LEFT JOIN app.c c ON id = parent_id AND tenant = c.tenant_ref " +
            "LEFT JOIN app.d d ON id = d.parent_id;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "c_note", "d_note" }, view.Columns.Select(c => c.Name));
        Assert.True(view.Columns[1].Nullable);
        Assert.True(view.Columns[2].Nullable);
        Assert.All(view.SourceBindings, x => Assert.NotEmpty(x.Columns));
    }

    [Fact]
    public void OnFollowedByUsingThenOnCanResolveNewMergedKey()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL, a_note TEXT);" +
            "CREATE TABLE app.b(a_id INTEGER NOT NULL, b_note TEXT);" +
            "CREATE TABLE app.c(id INTEGER NOT NULL, c_note TEXT);" +
            "CREATE TABLE app.d(parent_id INTEGER NOT NULL, d_note TEXT);" +
            "CREATE VIEW app.v AS SELECT id AS merged_id, d.d_note " +
            "FROM app.a a JOIN app.b b ON a.id = b.a_id " +
            "JOIN app.c c USING (id) " +
            "LEFT JOIN app.d d ON id = parent_id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Equal(new[] { "merged_id", "d_note" },
            Assert.Single(result.FinalCatalog.Views).Columns.Select(c => c.Name));
    }

    [Fact]
    public void QualifiedPhysicalOnOperandDoesNotLoseEarlierMergedLineage()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note, c.c_note " +
            "FROM app.a a FULL JOIN app.b b USING (id) " +
            "JOIN app.c c ON a.id = c.parent_id;";
        var result = Analyze(setup);
        Assert.Empty(result.Diagnostics);
        foreach (var t in new[] { "a", "b" })
        {
            var blocked = Analyze(setup + $"ALTER TABLE app.{t} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[5].Status);
        }
    }

    [Theory]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON id = id")]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON parent_id = c.parent_id")]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON tenant = tenant_ref")]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON missing = c.parent_id")]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON id = c.c_note")]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON a.id = b.id")]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON c.parent_id = c.tenant_ref")]
    [InlineData("SELECT a.a_note FROM app.a a JOIN app.b b USING (id) JOIN app.c c ON a.id = d.parent_id")]
    public void UnsafeOrUnboundOnOperandsStillFailClosed(string select)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + select + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.DoesNotContain(result.Statements[4].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnqualifiedOnIsAmbiguousWhenNewRightAlsoExposesId()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note " +
            "FROM app.a a JOIN app.b b USING (id) " +
            "JOIN app.d d ON id = d.parent_id " +
            "JOIN app.a x USING (id);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.FinalCatalog.Views);

        var ambiguous = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT a.a_note " +
            "FROM app.a a JOIN app.b b USING (id) " +
            "JOIN app.d d ON id = d.parent_id " +
            "JOIN app.c c ON tenant = c.tenant_ref;");
        // Prior LEFT visible tenant is not merged because previous USING was id only.
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, ambiguous.Statements[4].Status);
    }

    [Fact]
    public void DifferentTypeUniqueOnOperandIsRejectedWithoutCatalogMutation()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL);" +
            "CREATE TABLE app.b(id INTEGER NOT NULL);" +
            "CREATE TABLE app.c(parent_id TEXT);" +
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b USING (id) " +
            "JOIN app.c c ON id = parent_id;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NoOtherDialectCanMintPostgresStageVisibleOnProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT a.id FROM app.a a " +
            "JOIN app.b b USING (id) JOIN app.c c ON id = c.parent_id;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnknownCatalogCannotPublishStageVisibleOnView()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT id FROM app.a a JOIN app.b b USING (id) " +
            "JOIN app.c c ON id = c.parent_id;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task LivePostgresFullMergedOnUsesCoalescedBOnlyValueAndRealDropGuards()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required for real parity.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }
        var schema = "sqlcore_stageon_" + Guid.NewGuid().ToString("N");
        var setup =
            $"CREATE TABLE {schema}.a(id INTEGER NOT NULL, a_note TEXT);" +
            $"CREATE TABLE {schema}.b(id INTEGER NOT NULL, b_note TEXT);" +
            $"CREATE TABLE {schema}.c(parent_id INTEGER NOT NULL, c_note TEXT);" +
            $"CREATE VIEW {schema}.v AS SELECT id AS public_id, c.c_note " +
            $"FROM {schema}.a a FULL JOIN {schema}.b b USING (id) " +
            $"JOIN {schema}.c c ON id = parent_id;";
        var analyzed = Analyze(setup);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements,
            s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var view = Assert.Single(analyzed.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "c_note" }, view.Columns.Select(x => x.Name));

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Execute("CREATE SCHEMA " + schema);
        foreach (var statement in analyzed.Statements)
            await Execute(statement.Syntax.Text);
        await Execute($"INSERT INTO {schema}.a VALUES (1,'left'),(2,'left2');");
        await Execute($"INSERT INTO {schema}.b VALUES (1,'both'),(3,'only_b');");
        await Execute($"INSERT INTO {schema}.c VALUES (1,'match'),(3,'from_b');");
        var rows = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT public_id,c_note FROM {schema}.v ORDER BY public_id", db, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(reader.GetInt32(0) + "|" + reader.GetString(1));
        }
        Assert.Equal(new[] { "1|match", "3|from_b" }, rows);
        foreach (var table in new[] { "a", "b" })
        {
            var staticGuard = Analyze(setup +
                $"ALTER TABLE {schema}.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, staticGuard.Statements[4].Status);
            await Execute("SAVEPOINT stage_on_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Execute($"ALTER TABLE {schema}.{table} DROP COLUMN id;"));
            await Execute("ROLLBACK TO SAVEPOINT stage_on_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
