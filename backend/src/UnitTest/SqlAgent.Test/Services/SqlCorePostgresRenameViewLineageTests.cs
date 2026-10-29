using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL column rename changes physical View inputs, not the View's
/// persistent SQL-facing output header. All updates must be atomic and typed.
/// </summary>
public sealed class SqlCorePostgresRenameViewLineageTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            initial ?? SqlCatalogState.KnownEmpty(), null,
            TestContext.Current.CancellationToken);

    [Fact]
    public void DirectAndAliasedViewsRetargetPhysicalSourcesButNeverRenameOutputs()
    {
        var r = Analyze(
            "CREATE TABLE app.src (id INTEGER NOT NULL, label TEXT);" +
            "CREATE VIEW app.inferred AS SELECT id FROM app.src;" +
            "CREATE VIEW app.aliased (public_id) AS SELECT id AS internal_id FROM app.src;" +
            "ALTER TABLE app.src RENAME COLUMN id TO new_id;");

        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(SqlCatalogOperation.RenameColumn,
            Assert.Single(r.Statements[3].Changes).Operation);
        Assert.Single(r.Statements[3].DetailedEffects);
        Assert.True(r.FinalCatalog.IsClosedWorld);

        var before = r.Statements[3].CatalogBefore;
        var prior = before.Views.Single(v => v.Name == "app.inferred");
        var after = r.FinalCatalog.Views.Single(v => v.Name == "app.inferred");
        Assert.Equal("id", Assert.Single(prior.SourceColumns));
        Assert.Equal("new_id", Assert.Single(after.SourceColumns));
        Assert.Equal("id", Assert.Single(after.Columns).Name);
        Assert.Equal("INTEGER", Assert.Single(after.Columns).TypeName);
        Assert.False(Assert.Single(after.Columns).Nullable);
        Assert.Equal("app.src", Assert.Single(after.Dependencies)!.DisplayName);
        Assert.Equal("new_id", Assert.Single(Assert.Single(after.SourceBindings).Columns));
        var aliased = r.FinalCatalog.Views.Single(v => v.Name == "app.aliased");
        Assert.Equal("public_id", Assert.Single(aliased.Columns).Name);
        Assert.Equal("new_id", Assert.Single(aliased.SourceColumns));
        Assert.Equal("id", Assert.Single(before.Views.Single(
            v => v.Name == "app.aliased").SourceColumns));
        Assert.Equal(new[] { "new_id", "label" },
            Assert.Single(r.FinalCatalog.Tables).Columns.Select(x => x.Name));
    }

    [Fact]
    public void TransitiveViewsKeepPublicInterfaceAndExistingDependentBindings()
    {
        var r = Analyze(
            "CREATE TABLE app.src (id INTEGER, label TEXT);" +
            "CREATE VIEW app.v1 (public_id) AS SELECT id FROM app.src;" +
            "CREATE VIEW app.v2 AS SELECT public_id FROM app.v1;" +
            "ALTER TABLE app.src RENAME COLUMN id TO renamed_id;" +
            "CREATE VIEW app.v3 AS SELECT public_id FROM app.v2;");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        var v1 = r.FinalCatalog.Views.Single(v => v.Name == "app.v1");
        var v2 = r.FinalCatalog.Views.Single(v => v.Name == "app.v2");
        var v3 = r.FinalCatalog.Views.Single(v => v.Name == "app.v3");
        Assert.Equal("renamed_id", Assert.Single(v1.SourceColumns));
        Assert.Equal("public_id", Assert.Single(v1.Columns).Name);
        Assert.Equal("public_id", Assert.Single(v2.SourceColumns));
        Assert.Equal("app.v1", Assert.Single(v2.Dependencies)!.DisplayName);
        Assert.Equal("public_id", Assert.Single(v3.SourceColumns));
        Assert.Equal("app.v2", Assert.Single(v3.Dependencies)!.DisplayName);
        Assert.Equal("id", Assert.Single(r.Statements[3].CatalogBefore.Views.Single(
            v => v.Name == "app.v1").SourceColumns));
        Assert.Same(r.Statements[3].CatalogBefore.Views.Single(v => v.Name == "app.v2"),
            r.Statements[3].CatalogAfter.Views.Single(v => v.Name == "app.v2"));
    }

    [Fact]
    public void JoinKeyAndWhereOnlyReferencesRewriteExactlyTheirOwningRelation()
    {
        var r = Analyze(
            "CREATE TABLE app.orders (id INTEGER NOT NULL, title TEXT, status TEXT);" +
            "CREATE TABLE app.lines (id INTEGER, order_id INTEGER NOT NULL, amount INTEGER);" +
            "CREATE VIEW app.v (display_title, total) AS SELECT o.title, l.amount " +
            "FROM app.orders o JOIN app.lines l ON o.id = l.order_id " +
            "WHERE o.status = 'ready';" +
            "ALTER TABLE app.orders RENAME COLUMN id TO order_key;" +
            "ALTER TABLE app.orders RENAME COLUMN status TO state;" +
            "ALTER TABLE app.lines RENAME COLUMN order_id TO order_ref;");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, x => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        var view = Assert.Single(r.FinalCatalog.Views);
        Assert.Equal(new[] { "display_title", "total" }, view.Columns.Select(c => c.Name));
        Assert.Equal("app.orders", view.SourceBindings[0].Relation.DisplayName);
        Assert.Equal(new[] { "title", "order_key", "state" }, view.SourceBindings[0].Columns);
        Assert.Equal("app.lines", view.SourceBindings[1].Relation.DisplayName);
        Assert.Equal(new[] { "amount", "order_ref" }, view.SourceBindings[1].Columns);
        Assert.Equal(new[] { "title", "order_key", "state", "amount", "order_ref" },
            view.SourceColumns);
        Assert.Equal(new[] { "title", "id", "status", "amount", "order_id" },
            r.Statements[3].CatalogBefore.Views.Single().SourceColumns);
    }

    [Fact]
    public void DropColumnStillRejectsRenamedViewInputNotOldOrUnrelatedInputs()
    {
        const string setup =
            "CREATE TABLE app.src (id INTEGER, label TEXT, spare INTEGER);" +
            "CREATE VIEW app.v (public_id) AS SELECT id FROM app.src WHERE label = 'x';" +
            "ALTER TABLE app.src RENAME COLUMN id TO new_id;";
        var blocked = Analyze(setup + "ALTER TABLE app.src DROP COLUMN new_id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, blocked.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[3].Status);
        Assert.Empty(blocked.Statements[3].Changes);
        Assert.Same(blocked.Statements[3].CatalogBefore,
            blocked.Statements[3].CatalogAfter);
        Assert.Equal("new_id", Assert.Single(blocked.FinalCatalog.Views).SourceColumns[0]);
        var allowed = Analyze(setup + "ALTER TABLE app.src DROP COLUMN spare;");
        Assert.Empty(allowed.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[3].Status);
        Assert.Equal(new[] { "new_id", "label" },
            Assert.Single(allowed.FinalCatalog.Tables).Columns.Select(c => c.Name));
        Assert.Equal(new[] { "new_id", "label" },
            Assert.Single(allowed.FinalCatalog.Views).SourceColumns);
    }

    [Fact]
    public void RenameWithInboundForeignKeyAndDependentViewPublishesOneAtomicCatalog()
    {
        var r = Analyze(
            "CREATE TABLE app.parent (id INTEGER NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.child (pid INTEGER, CONSTRAINT fk_child " +
            "FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "CREATE VIEW app.v (external_id) AS SELECT id FROM app.parent;" +
            "ALTER TABLE app.parent RENAME COLUMN id TO renamed_id;");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal("id", r.Statements[3].CatalogBefore.Tables.Single(
            t => t.Name == "app.parent").Columns.Single().Name);
        Assert.Equal("id", r.Statements[3].CatalogBefore.Tables.Single(
            t => t.Name == "app.child").Constraints.Single().ReferencedColumns[0]);
        Assert.Equal("id", Assert.Single(r.Statements[3].CatalogBefore.Views.Single().SourceColumns));
        Assert.Equal("renamed_id", Assert.Single(r.FinalCatalog.Tables.Single(
            t => t.Name == "app.child").Constraints.Single().ReferencedColumns));
        Assert.Equal("renamed_id", Assert.Single(r.FinalCatalog.Views.Single().SourceColumns));
        Assert.Equal("external_id", Assert.Single(r.FinalCatalog.Views.Single().Columns).Name);
    }

    [Fact]
    public void RepeatedRenameAndQuotedIdentifiersPreservePhysicalProvenance()
    {
        var r = Analyze(
            "CREATE TABLE app.\"weird.table\" (\"Case.Id\" INTEGER, note TEXT);" +
            "CREATE VIEW app.v (external_id) AS SELECT \"Case.Id\" FROM app.\"weird.table\";" +
            "ALTER TABLE app.\"weird.table\" RENAME COLUMN \"Case.Id\" TO \"Next.Id\";" +
            "ALTER TABLE app.\"weird.table\" RENAME COLUMN \"Next.Id\" TO \"Final.Id\";");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal("Final.Id", Assert.Single(r.FinalCatalog.Views.Single().SourceColumns));
        Assert.Equal("external_id", Assert.Single(r.FinalCatalog.Views.Single().Columns).Name);
        Assert.Equal("Case.Id", Assert.Single(r.Statements[2].CatalogBefore.Views.Single().SourceColumns));
    }

    [Fact]
    public void OpenWorldDoesNotProveRenamesEvenWithViews()
    {
        var r = Analyze("ALTER TABLE app.src RENAME COLUMN id TO new_id;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(r.Statements).Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
