using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Bounded PostgreSQL View ON grouping: parentheses may group only typed
/// equality conjunctions. They must never weaken source/dependency proofs.
/// </summary>
public sealed class SqlCorePostgresViewGroupedOnProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, tenant INTEGER NOT NULL, label TEXT);" +
        "CREATE TABLE app.lines(line_id INTEGER NOT NULL, order_id INTEGER NOT NULL, tenant INTEGER NOT NULL);" +
        "CREATE TABLE app.shipments(ship_id INTEGER NOT NULL, line_id INTEGER NOT NULL, tenant INTEGER NOT NULL);";

    private static SqlScriptAnalysisResult Analyze(string sql, SqlCatalogState? catalog = null,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("(o.id = l.order_id)")]
    [InlineData("((o.id = l.order_id))")]
    [InlineData("(o.id = l.order_id AND o.tenant = l.tenant)")]
    [InlineData("(o.id = l.order_id) AND (o.tenant = l.tenant)")]
    [InlineData("((o.id = l.order_id) AND ((o.tenant = l.tenant)))")]
    public void GroupedEqualityAndConjunctionsBindPhysicalColumns(string on)
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON " + on + ";";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "app.orders", "app.lines" },
            view.Dependencies.Select(x => x!.DisplayName));
        Assert.Equal(new[] { "id" }.Concat(on.Contains("tenant", StringComparison.Ordinal)
                ? new[] { "tenant" } : Array.Empty<string>()),
            view.SourceBindings[0]!.Columns);
        Assert.Equal(new[] { "order_id" }.Concat(on.Contains("tenant", StringComparison.Ordinal)
                ? new[] { "tenant" } : Array.Empty<string>()),
            view.SourceBindings[1]!.Columns);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void GroupedEdgesPreserveNullabilityWhereAndPerSourceDropGuards()
    {
        var script = Tables +
            "CREATE VIEW app.v (order_id, line_id, ship_id) AS " +
            "SELECT o.id, l.line_id, s.ship_id FROM app.orders o " +
            "JOIN app.lines l ON ((o.id = l.order_id) AND (o.tenant = l.tenant)) " +
            "LEFT JOIN app.shipments s ON (((l.line_id = s.line_id)) AND " +
            "(o.tenant = s.tenant)) WHERE o.label = 'paid';";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { false, false, true }, view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "id", "tenant", "label" }, view.SourceBindings[0]!.Columns);
        Assert.Equal(new[] { "line_id", "order_id", "tenant" }, view.SourceBindings[1]!.Columns);
        Assert.Equal(new[] { "ship_id", "line_id", "tenant" }, view.SourceBindings[2].Columns);

        foreach (var (table, column) in new[]
        {
            ("orders", "tenant"), ("orders", "label"),
            ("lines", "order_id"), ("shipments", "tenant")
        })
        {
            var blocked = Analyze(script + $"ALTER TABLE app.{table} DROP COLUMN {column};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
        }
    }

    [Theory]
    [InlineData("(o.id = l.order_id OR o.id = o.tenant)")]
    [InlineData("(o.id > o.tenant)")]
    [InlineData("((o.id = l.order_id) OR (o.id = o.tenant))")]
    [InlineData("(o.id = l.order_id AND)")]
    [InlineData("()")]
    [InlineData("(o.id = l.order_id) AND (o.id = l.order_id) AND (o.id = l.order_id) AND (o.id = l.order_id) AND (o.id = l.order_id) AND (o.id = l.order_id) AND (o.id = l.order_id) AND (o.id = l.order_id) AND (o.id = l.order_id)")]
    public void UnknownOrInvalidGroupingCannotPublishView(string on)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o JOIN app.lines l ON " + on + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void LexicallyUnbalancedOnFailsClosedBeforeScriptAnalysis()
    {
        Assert.Throws<SqlParseException>(() => Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o JOIN app.lines l " +
            "ON (o.id = l.order_id;"));
    }

    [Fact]
    public void OnParenthesesDepthIsBounded()
    {
        var on = new string('(', 18) + "o.id = l.order_id" + new string(')', 18);
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o JOIN app.lines l ON " + on + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectCannotClaimPostgresGroupedViewProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON ((o.id = l.order_id));", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenWorldCannotClaimGroupedViewProof()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON ((o.id = l.order_id));", SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public async Task GroupedOnMatchesLivePostgresRowsAndDependencyFences()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Live PostgreSQL parity needs TEST_POSTGRES_CONNECTION in CI.");
            Assert.Skip("Live PostgreSQL parity needs TEST_POSTGRES_CONNECTION.");
            return;
        }

        var schema = "sqlcore_grouped_on_" + Guid.NewGuid().ToString("N");
        var script =
            $"CREATE TABLE {schema}.orders(id INTEGER NOT NULL, tenant INTEGER NOT NULL);" +
            $"CREATE TABLE {schema}.lines(line_id INTEGER NOT NULL, order_id INTEGER NOT NULL, tenant INTEGER NOT NULL);" +
            $"CREATE VIEW {schema}.v (order_id, line_id) AS " +
            $"SELECT o.id, l.line_id FROM {schema}.orders o LEFT JOIN {schema}.lines l " +
            "ON (((o.id = l.order_id)) AND ((o.tenant = l.tenant)));";
        var model = Analyze(script);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements,
            statement => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Equal(new[] { false, true },
            Assert.Single(model.FinalCatalog.Views).Columns.Select(c => c.Nullable));

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Execute("CREATE SCHEMA " + schema);
        foreach (var statement in model.Statements)
            await Execute(statement.Syntax.Text);
        await Execute($"INSERT INTO {schema}.orders(id,tenant) VALUES (1,7),(2,7);");
        await Execute($"INSERT INTO {schema}.lines(line_id,order_id,tenant) VALUES (10,1,7),(20,2,8);");

        var rows = new List<(int, int?)>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT order_id, line_id FROM {schema}.v ORDER BY order_id", db, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1)));
        }
        Assert.Equal(new[] { (1, (int?)10), (2, (int?)null) }, rows);
        var blocked = Analyze(script + $"ALTER TABLE {schema}.orders DROP COLUMN tenant;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[3].Status);
        await using (var cmd = new NpgsqlCommand(
            $"ALTER TABLE {schema}.orders DROP COLUMN tenant", db, tx))
            await Assert.ThrowsAsync<PostgresException>(() =>
                cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
