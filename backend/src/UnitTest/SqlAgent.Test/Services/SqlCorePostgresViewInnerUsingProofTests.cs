using System;
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
/// PostgreSQL two-source INNER USING: typed same-name key binding, merged
/// unqualified name resolution and exact physical dependency ownership.
/// </summary>
public sealed class SqlCorePostgresViewInnerUsingProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, tenant INTEGER NOT NULL, title TEXT);" +
        "CREATE TABLE app.lines(id INTEGER NOT NULL, tenant INTEGER NOT NULL, amount INTEGER);" +
        "CREATE TABLE app.extra(id INTEGER NOT NULL, tenant INTEGER NOT NULL, note TEXT);";

    private static SqlScriptAnalysisResult Analyze(string script,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(script, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN")]
    [InlineData("INNER JOIN")]
    public void MergedKeyAndQualifiedColumnsArePhysicallyOwned(string join)
    {
        var sql = Tables + "CREATE VIEW app.v AS " +
            "SELECT id AS shared_id, o.title, l.amount FROM app.orders o " +
            join + " app.lines l USING (id,tenant) WHERE id > 0;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var v = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "shared_id", "title", "amount" }, v.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "app.orders", "app.lines" },
            v.Dependencies.Select(c => c!.DisplayName));
        Assert.Equal(new[] { "id", "title", "tenant" }, v.SourceBindings[0].Columns);
        Assert.Equal(new[] { "amount", "id", "tenant" }, v.SourceBindings[1].Columns);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SingleUsingColumnAllowsUnqualifiedMergedKeyWithoutLeakingOtherDuplicateNames()
    {
        var valid = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id, o.title, l.amount FROM app.orders o " +
            "JOIN app.lines l USING (id);");
        Assert.Empty(valid.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, valid.Statements[3].Status);
        var view = Assert.Single(valid.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "title", "amount" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "title" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "amount", "id" }, view.SourceBindings[1].Columns);

        var ambiguous = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT tenant FROM app.orders o JOIN app.lines l USING (id);");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, ambiguous.Statements[3].Status);
        Assert.Empty(ambiguous.FinalCatalog.Views);
    }

    [Fact]
    public void QualifiedWildcardRetainsColumnsButBareWildcardDuplicateNonkeysFail()
    {
        var qualified = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.* FROM app.orders o JOIN app.lines l USING (id);");
        Assert.Empty(qualified.Diagnostics);
        var view = Assert.Single(qualified.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "tenant", "title" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "tenant", "title" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "id" }, view.SourceBindings[1].Columns);

        var bare = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT * FROM app.orders o JOIN app.lines l USING (id);");
        // Both relations also expose tenant, which is not part of USING(id).
        // PostgreSQL refuses duplicate final View column names.
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, bare.Statements[3].Status);
        Assert.Empty(bare.FinalCatalog.Views);
    }

    [Fact]
    public void BothUsingKeysAreProtectedAndUnusedColumnsRemainDroppable()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT o.title FROM app.orders o " +
            "JOIN app.lines l USING (id,tenant);";
        foreach (var table in new[] { "orders", "lines" })
        foreach (var column in new[] { "id", "tenant" })
        {
            var blocked = Analyze(setup + $"ALTER TABLE app.{table} DROP COLUMN {column};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
        }
        var allowed = Analyze(setup + "ALTER TABLE app.lines DROP COLUMN amount;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[4].Status);
    }

    [Theory]
    [InlineData("SELECT id FROM app.orders o JOIN app.lines l USING (id,id)")]
    [InlineData("SELECT id FROM app.orders o JOIN app.lines l USING (missing)")]
    [InlineData("SELECT id FROM app.orders o JOIN app.lines l USING (id,missing)")]
    [InlineData("SELECT id FROM app.orders o JOIN app.lines l USING ()")]
    [InlineData("SELECT id FROM app.orders o LEFT JOIN app.lines l USING (id)")]
    [InlineData("SELECT id FROM app.orders o RIGHT JOIN app.lines l USING (id)")]
    [InlineData("SELECT id FROM app.orders o FULL JOIN app.lines l USING (id)")]
    [InlineData("SELECT id FROM app.orders o JOIN app.lines l USING (id) JOIN app.extra e USING (id)")]
    [InlineData("SELECT id FROM app.orders o JOIN app.lines l USING (id) WHERE o.id = l.id")]
    public void UnsupportedOrInvalidUsingNeverMintsKnownView(string select)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + select + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void IncompatibleKeyTypesAreRejectedWithoutCatalogMutation()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER NOT NULL);" +
            "CREATE TABLE app.b(id TEXT);" +
            "CREATE VIEW app.v AS SELECT a.id FROM app.a a JOIN app.b b USING (id);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonPostgresCannotClaimInnerUsingProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT id FROM app.orders o JOIN app.lines l USING (id);",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnknownCatalogCannotMintUsingProof()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT id FROM app.orders o JOIN app.lines l USING (id);",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task InnerUsingRowsColumnsAndPhysicalDropGuardsMatchLivePostgres()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required for real PostgreSQL parity.");
            Assert.Skip("Live PostgreSQL parity requires TEST_POSTGRES_CONNECTION.");
            return;
        }
        var schema = "sqlcore_using_" + Guid.NewGuid().ToString("N");
        var setup =
            $"CREATE TABLE {schema}.orders(id INTEGER NOT NULL, tenant INTEGER NOT NULL, title TEXT);" +
            $"CREATE TABLE {schema}.lines(id INTEGER NOT NULL, tenant INTEGER NOT NULL, amount INTEGER);" +
            $"CREATE VIEW {schema}.v AS SELECT id AS public_id, o.title, l.amount " +
            $"FROM {schema}.orders o INNER JOIN {schema}.lines l USING (id,tenant);";
        var analyzed = Analyze(setup);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements,
            s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(new[] { "public_id", "title", "amount" },
            Assert.Single(analyzed.FinalCatalog.Views).Columns.Select(c => c.Name));

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
        await Execute($"INSERT INTO {schema}.orders VALUES (1,7,'ok'),(2,7,'mismatch');");
        await Execute($"INSERT INTO {schema}.lines VALUES (1,7,100),(2,8,999);");
        await using (var cmd = new NpgsqlCommand(
            $"SELECT public_id,title,amount FROM {schema}.v", db, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("ok", reader.GetString(1));
            Assert.Equal(100, reader.GetInt32(2));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        foreach (var target in new[] { "orders", "lines" })
        {
            var staticDrop = Analyze(setup + $"ALTER TABLE {schema}.{target} DROP COLUMN tenant;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, staticDrop.Statements[3].Status);
            await Execute("SAVEPOINT using_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Execute($"ALTER TABLE {schema}.{target} DROP COLUMN tenant;"));
            await Execute("ROLLBACK TO SAVEPOINT using_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
