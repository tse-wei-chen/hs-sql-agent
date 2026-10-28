using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Proves a bounded, left-deep multi-source JOIN graph with typed
/// equality conjunctions and conservative nullability propagation.</summary>
public sealed class SqlCorePostgresViewJoinGraphProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, tenant INTEGER NOT NULL, label TEXT);" +
        "CREATE TABLE app.lines(line_id INTEGER NOT NULL, order_id INTEGER NOT NULL, tenant INTEGER NOT NULL, amount INTEGER NOT NULL);" +
        "CREATE TABLE app.shipments(ship_id INTEGER NOT NULL, line_id INTEGER NOT NULL, tenant INTEGER NOT NULL, tracking TEXT);" +
        "CREATE TABLE app.backups(backup_id INTEGER NOT NULL, shipment_id INTEGER NOT NULL);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, initial ?? SqlCatalogState.KnownEmpty());

    [Fact]
    public void ThreeSourcesAndCompoundOnEachEdgeBindOwnedColumns()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (order_number, line_number, tracking_code) AS " +
            "SELECT o.id, l.line_id, s.tracking FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id AND o.tenant = l.tenant " +
            "JOIN app.shipments s ON l.line_id = s.line_id AND o.tenant = s.tenant;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "order_number", "line_number", "tracking_code" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { false, false, true },
            view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "app.orders", "app.lines", "app.shipments" },
            view.Dependencies.Select(c => c.DisplayName));
        Assert.Equal(new[] { "id", "tenant" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "line_id", "order_id", "tenant" }, view.SourceBindings[1].Columns);
        Assert.Equal(new[] { "tracking", "line_id", "tenant" }, view.SourceBindings[2].Columns);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("JOIN", "LEFT JOIN", false, false, true)]
    [InlineData("LEFT JOIN", "RIGHT OUTER JOIN", true, true, false)]
    [InlineData("RIGHT JOIN", "FULL OUTER JOIN", true, true, true)]
    [InlineData("FULL JOIN", "INNER JOIN", true, true, false)]
    [InlineData("FULL JOIN", "LEFT JOIN", true, true, true)]
    public void JoinGraphPropagatesNullExtensionAcrossAllPriorSources(
        string first, string second, bool leftNullable, bool middleNullable, bool rightNullable)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (order_id, line_id, ship_id) AS " +
            "SELECT o.id, l.line_id, s.ship_id FROM app.orders o " + first +
            " app.lines l ON o.id = l.order_id " + second +
            " app.shipments s ON l.line_id = s.line_id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { leftNullable, middleNullable, rightNullable },
            Assert.Single(result.FinalCatalog.Views).Columns.Select(c => c.Nullable));
    }

    [Fact]
    public void FourSourcesAreBoundedAndEachJoinEdgeIsChecked()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id, l.line_id, s.ship_id, b.backup_id " +
            "FROM app.orders o JOIN app.lines l ON o.id = l.order_id " +
            "LEFT JOIN app.shipments s ON l.line_id = s.line_id " +
            "RIGHT JOIN app.backups b ON s.ship_id = b.shipment_id;");
        Assert.Empty(result.Diagnostics);
        var v = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(4, v.Dependencies.Count);
        Assert.Equal(new[] { true, true, true, false }, v.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "ship_id", "line_id" }, v.SourceBindings[2].Columns);
        Assert.Equal(new[] { "backup_id", "shipment_id" }, v.SourceBindings[3].Columns);
    }

    [Fact]
    public void QualifiedSourceWildcardAndWhereOnlyDependencyWorkAcrossThreeRelations()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT s.* FROM app.orders o " +
            "LEFT JOIN app.lines l ON o.id = l.order_id " +
            "LEFT JOIN app.shipments s ON l.line_id = s.line_id " +
            "WHERE o.label = 'paid' AND s.tracking IS NOT NULL;";
        var result = Analyze(setup);
        Assert.Empty(result.Diagnostics);
        Assert.All(Assert.Single(result.FinalCatalog.Views).Columns, c => Assert.True(c.Nullable));
        var v = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "label" }, v.SourceBindings[0].Columns);
        Assert.Equal(new[] { "order_id", "line_id" }, v.SourceBindings[1].Columns);
        Assert.Equal(new[] { "ship_id", "line_id", "tenant", "tracking" },
            v.SourceBindings[2].Columns);
        var blocked = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN label;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[5].Status);
        var allowed = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN tenant;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[5].Status);
    }

    [Fact]
    public void ReplacingCompoundGraphRewiresAllPhysicalSourceDependencies()
    {
        var setup = Tables +
            "CREATE VIEW app.v (order_id) AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id " +
            "JOIN app.shipments s ON l.line_id = s.line_id;" +
            "CREATE VIEW app.child AS SELECT order_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v (order_id) AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id " +
            "JOIN app.backups b ON l.line_id = b.shipment_id;";
        var result = Analyze(setup);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[6].Changes).Operation);
        Assert.Equal(new[] { "app.orders", "app.lines", "app.backups" },
            result.FinalCatalog.Views.Single(v => v.Name == "app.v")
                .Dependencies.Select(d => d.DisplayName));
        var oldSourceDrop = Analyze(setup + "ALTER TABLE app.shipments DROP COLUMN line_id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, oldSourceDrop.Statements[7].Status);
        var newSourceDrop = Analyze(setup + "ALTER TABLE app.backups DROP COLUMN shipment_id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, newSourceDrop.Statements[7].Status);
    }

    [Theory]
    [InlineData("JOIN")]
    [InlineData("LEFT JOIN")]
    [InlineData("FULL JOIN")]
    public void IntermediateTableRestrictAndCascadeProtectEntireGraph(string second)
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id " + second +
            " app.shipments s ON l.line_id = s.line_id;" +
            "CREATE VIEW app.child AS SELECT id FROM app.v;";
        var restricted = Analyze(setup + "DROP TABLE app.lines;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, restricted.Statements[6].Status);
        var cascade = Analyze(setup + "DROP TABLE app.lines CASCADE;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, cascade.Statements[6].Status);
        Assert.Empty(cascade.FinalCatalog.Views);
        Assert.Equal(3, cascade.FinalCatalog.Tables.Count);
    }

    [Theory]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON o.id = l.order_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id AND o.tenant = s.tenant JOIN app.shipments s ON l.line_id = s.line_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON l.line_id = s.tracking")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id JOIN app.shipments l ON l.line_id = l.line_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON l.line_id = bad.line_id")]
    [InlineData("SELECT tenant FROM app.orders o JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON l.line_id = s.line_id")]
    public void CrossEdgeOrAmbiguousBindingsNeverPublishKnownView(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[4].Status);
        Assert.Empty(result.Statements[4].Changes);
        Assert.Same(result.Statements[4].CatalogBefore, result.Statements[4].CatalogAfter);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id OR o.tenant = l.tenant")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id > l.order_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l USING (order_id)")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id CROSS JOIN app.shipments s")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON (l.line_id = s.line_id)")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON l.line_id = s.line_id JOIN app.backups b ON s.ship_id = b.shipment_id JOIN app.orders x ON x.id = o.id")]
    public void UnmodeledJoinGrammarRemainsFailClosed(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.DoesNotContain(result.Statements[4].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherFiveDialectsDoNotReceivePostgresJoinGraphProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON l.line_id = s.line_id;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
    }

    [Fact]
    public void UnknownCatalogNeverMintsGraphProof()
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id JOIN app.shipments s ON l.line_id = s.line_id;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
