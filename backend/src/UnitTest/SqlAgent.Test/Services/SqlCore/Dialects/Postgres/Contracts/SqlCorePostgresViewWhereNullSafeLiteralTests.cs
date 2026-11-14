using System;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL CREATE VIEW WHERE supports typed, NULL-safe comparisons against
/// bounded scalar literals without fabricating SQL execution proof.
/// </summary>
public sealed class SqlCorePostgresViewWhereNullSafeLiteralTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, qty INTEGER, status TEXT, deleted BOOLEAN);" +
        "CREATE TABLE app.other(id INTEGER NOT NULL, qty INTEGER, status TEXT, deleted BOOLEAN);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("WHERE qty IS DISTINCT FROM 3", "qty")]
    [InlineData("WHERE qty IS NOT DISTINCT FROM 3", "qty")]
    [InlineData("WHERE o.qty IS DISTINCT FROM -7", "qty")]
    [InlineData("WHERE o.qty IS NOT DISTINCT FROM 0", "qty")]
    [InlineData("WHERE status IS DISTINCT FROM 'ready'", "status")]
    [InlineData("WHERE o.status IS NOT DISTINCT FROM 'ready'", "status")]
    [InlineData("WHERE deleted IS DISTINCT FROM TRUE", "deleted")]
    [InlineData("WHERE o.deleted IS NOT DISTINCT FROM FALSE", "deleted")]
    public void TypedNullSafeLiteralWhereRetainsFilterColumnEvidence(
        string predicate, string column)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o " + predicate + ";");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[2].EffectCoverage);
        Assert.Equal(SqlCatalogOperation.CreateView,
            Assert.Single(result.Statements[2].Changes).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", column }, view.SourceColumns);
        Assert.Equal("app.orders", Assert.Single(view.Dependencies)!.DisplayName);
    }

    [Theory]
    [InlineData("WHERE (qty IS DISTINCT FROM 1 OR status IS NOT DISTINCT FROM 'ready') AND deleted = TRUE")]
    [InlineData("WHERE qty > 0 AND (status IS DISTINCT FROM 'cancelled' OR deleted IS NOT DISTINCT FROM FALSE)")]
    [InlineData("WHERE qty IS NOT DISTINCT FROM 5 OR (status IS DISTINCT FROM 'ready' AND deleted IS NULL)")]
    public void NullSafePredicatesCombineWithoutLosingPhysicalDependencies(string predicate)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id FROM app.orders " + predicate + ";");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "qty", "status", "deleted" }, view.SourceColumns);
    }

    [Fact]
    public void NullSafeFilterOnlyColumnBlocksDropAndReplacementRebindsItsOwner()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT id FROM app.orders " +
            "WHERE qty IS DISTINCT FROM 3;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT id FROM app.other " +
            "WHERE status IS NOT DISTINCT FROM 'ready';";
        var result = Analyze(setup);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[3].Changes).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "status" }, view.SourceColumns);
        Assert.Equal("app.other", Assert.Single(view.Dependencies)!.DisplayName);

        var oldDrop = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN qty;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, oldDrop.Statements[4].Status);
        var protectedDrop = Analyze(setup + "ALTER TABLE app.other DROP COLUMN status;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, protectedDrop.Statements[4].Status);
        Assert.Empty(protectedDrop.Statements[4].Changes);
        Assert.Same(protectedDrop.Statements[4].CatalogBefore,
            protectedDrop.Statements[4].CatalogAfter);
    }

    [Theory]
    [InlineData("WHERE qty IS DISTINCT FROM 'bad'")]
    [InlineData("WHERE status IS NOT DISTINCT FROM 1")]
    [InlineData("WHERE deleted IS DISTINCT FROM 1")]
    [InlineData("WHERE missing IS DISTINCT FROM 3")]
    [InlineData("WHERE o.qty IS DISTINCT FROM 3")]
    [InlineData("WHERE qty IS DISTINCT 3")]
    [InlineData("WHERE qty IS NOT DISTINCT 3")]
    [InlineData("WHERE qty IS DISTINCT FROM NULL")]
    [InlineData("WHERE qty IS DISTINCT FROM qty")]
    [InlineData("WHERE qty IS DISTINCT FROM 1 + 1")]
    public void InvalidOrUnprovenNullSafeFilterFailsClosed(string predicate)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id FROM app.orders " + predicate + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsDoNotInheritPostgresNullSafeWhereProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT id FROM app.orders " +
            "WHERE qty IS DISTINCT FROM 3;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenCatalogCannotClaimNullSafeLiteralWhereProof()
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT id FROM app.orders " +
            "WHERE qty IS DISTINCT FROM 3;", SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task LivePostgresNullSafeWhereDiffersFromOrdinarySqlNullComparison()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required for parity.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
            return;
        }

        var schema = "sqlcore_where_nullsafe_" + Guid.NewGuid().ToString("N");
        var sql =
            $"CREATE TABLE {schema}.data(id INTEGER NOT NULL, qty INTEGER);" +
            $"CREATE VIEW {schema}.v_distinct AS SELECT id FROM {schema}.data WHERE qty IS DISTINCT FROM 1;" +
            $"CREATE VIEW {schema}.v_same AS SELECT id FROM {schema}.data WHERE qty IS NOT DISTINCT FROM 1;" +
            $"CREATE VIEW {schema}.v_ordinary AS SELECT id FROM {schema}.data WHERE qty <> 1;";
        var analyzed = Analyze(sql);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements, stmt =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, stmt.Status));

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Execute(string command)
        {
            await using var cmd = new NpgsqlCommand(command, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Execute("CREATE SCHEMA " + schema);
        foreach (var statement in analyzed.Statements)
            await Execute(statement.Syntax.Text);
        await Execute($"INSERT INTO {schema}.data VALUES (1,0),(2,1),(3,NULL);");

        async Task<long> Count(string view)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM {schema}.{view}", db, tx);
            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
        Assert.Equal(2L, await Count("v_distinct")); // 0 and NULL differ from 1.
        Assert.Equal(1L, await Count("v_same")); // only 1 is not distinct from 1.
        Assert.Equal(1L, await Count("v_ordinary")); // SQL UNKNOWN filters out NULL.

        await Execute("SAVEPOINT where_nullsafe_dependency");
        await Assert.ThrowsAsync<PostgresException>(() =>
            Execute($"ALTER TABLE {schema}.data DROP COLUMN qty;"));
        await Execute("ROLLBACK TO SAVEPOINT where_nullsafe_dependency");
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
