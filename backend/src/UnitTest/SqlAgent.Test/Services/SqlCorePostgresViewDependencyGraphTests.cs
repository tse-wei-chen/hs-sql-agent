using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Proven PostgreSQL view dependencies are separate relation identities,
/// never disguised as ordinary table rows or runnable SQL plans.
/// </summary>
public sealed class SqlCorePostgresViewDependencyGraphTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, initial ?? SqlCatalogState.KnownEmpty());

    private const string Chain =
        "CREATE TABLE app.parent(id INTEGER, age INTEGER);" +
        "CREATE VIEW app.v_parent AS SELECT * FROM app.parent;" +
        "CREATE VIEW app.v_child AS SELECT id FROM app.v_parent;";

    [Fact]
    public void ViewChainCarriesTypedDependencyIdentitiesAndProjectedColumns()
    {
        var r = Analyze(Chain);
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.True(r.FinalCatalog.IsClosedWorld);
        Assert.Single(r.FinalCatalog.Tables);
        Assert.Equal(2, r.FinalCatalog.Views.Count);
        var parentView = r.FinalCatalog.Views.Single(v => v.Name == "app.v_parent");
        var childView = r.FinalCatalog.Views.Single(v => v.Name == "app.v_child");
        Assert.Equal("app.parent", parentView.Dependencies.Single().DisplayName);
        Assert.Equal("app.v_parent", childView.Dependencies.Single().DisplayName);
        Assert.Equal(2, parentView.Columns.Count);
        Assert.Equal("id", Assert.Single(childView.Columns).Name);
        Assert.Equal(SqlCatalogOperation.CreateView, Assert.Single(r.Statements[1].Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.CreateView,
            Assert.Single(r.Statements[1].DetailedEffects).Operation);
        Assert.All(r.Statements[1].Changes, x => Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty));
    }

    [Fact]
    public void RestrictCannotDropTableWithTransitiveViewDependents()
    {
        var r = Analyze(Chain + "DROP TABLE app.parent RESTRICT;");
        var drop = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
        Assert.Equal(2, r.FinalCatalog.Views.Count);
    }

    [Fact]
    public void CascadeDropsTableAndEntireTransitiveViewChain()
    {
        var r = Analyze(Chain + "DROP TABLE app.parent CASCADE;");
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlEffectCoverage.Complete, drop.EffectCoverage);
        Assert.Equal(new[] { SqlCatalogOperation.DropTable, SqlCatalogOperation.DropView,
                             SqlCatalogOperation.DropView },
            drop.Changes.Select(x => x.Operation).ToArray());
        Assert.Equal(new[] { "app.parent", "app.v_parent", "app.v_child" },
            drop.Changes.Select(x => x.TableName).ToArray());
        Assert.All(drop.Changes, x => Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty));
        Assert.Equal(3, drop.DetailedEffects.Count);
        Assert.All(drop.DetailedEffects, x => Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty));
        Assert.Empty(r.FinalCatalog.Tables);
        Assert.Empty(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void CascadeWithSurvivingForeignKeyDropsFkAndViewButNotChildTable()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE app.child(parent_id INTEGER, " +
            "CONSTRAINT fk_parent FOREIGN KEY(parent_id) REFERENCES app.parent(id));" +
            "CREATE VIEW app.v_child AS SELECT * FROM app.child;" +
            "CREATE VIEW app.v_parent AS SELECT * FROM app.parent;" +
            "DROP TABLE app.parent CASCADE;");
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Contains(drop.Changes, x =>
            x.Operation == SqlCatalogOperation.DropConstraint && x.ConstraintName == "fk_parent");
        Assert.Contains(drop.Changes, x =>
            x.Operation == SqlCatalogOperation.DropView && x.TableName == "app.v_parent");
        Assert.DoesNotContain(drop.Changes, x =>
            x.Operation == SqlCatalogOperation.DropView && x.TableName == "app.v_child");
        Assert.Equal("app.child", Assert.Single(r.FinalCatalog.Tables).Name);
        Assert.Equal("app.v_child", Assert.Single(r.FinalCatalog.Views).Name);
        Assert.Empty(Assert.Single(r.FinalCatalog.Tables).Constraints);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void BatchDropRestrictRejectsViewDependentOnEitherTable()
    {
        var r = Analyze(
            "CREATE TABLE app.a(id INTEGER);" +
            "CREATE TABLE app.b(id INTEGER);" +
            "CREATE VIEW app.v AS SELECT * FROM app.b;" +
            "DROP TABLE app.a, app.b;");
        var drop = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Equal(2, r.FinalCatalog.Tables.Count);
        Assert.Single(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void BatchDropCascadeDeletesViewClosureAfterAllTargetsProved()
    {
        var r = Analyze(
            Chain +
            "CREATE TABLE app.second(id INTEGER);" +
            "CREATE VIEW app.v_second AS SELECT * FROM app.second;" +
            "DROP TABLE app.parent, app.second CASCADE;");
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[5];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(2, drop.Changes.Count(x => x.Operation == SqlCatalogOperation.DropTable));
        Assert.Equal(3, drop.Changes.Count(x => x.Operation == SqlCatalogOperation.DropView));
        Assert.Empty(r.FinalCatalog.Tables);
        Assert.Empty(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DropViewRestrictBlocksDependentViewButCascadeDropsChain()
    {
        var restricted = Analyze(Chain + "DROP VIEW app.v_parent RESTRICT;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, restricted.Statements[3].Status);
        Assert.Equal(2, restricted.FinalCatalog.Views.Count);
        var cascade = Analyze(Chain + "DROP VIEW app.v_parent CASCADE;");
        Assert.Empty(cascade.Diagnostics);
        Assert.Equal(2, cascade.Statements[3].Changes.Count);
        Assert.All(cascade.Statements[3].Changes,
            c => Assert.Equal(SqlCatalogOperation.DropView, c.Operation));
        Assert.Single(cascade.FinalCatalog.Tables);
        Assert.Empty(cascade.FinalCatalog.Views);
        Assert.True(cascade.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DropViewIfExistsCanProveAbsentAndPreservesTables()
    {
        var r = Analyze("DROP VIEW IF EXISTS app.no_view;");
        var ddl = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Empty(ddl.Changes);
        Assert.Same(ddl.CatalogBefore, ddl.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ViewAndTableShareRelationNameAndWrongKindCannotBeDropped()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE VIEW app.parent AS SELECT * FROM app.parent;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, r.Statements[1].Status);
        Assert.Empty(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
        var s = Analyze(
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE VIEW app.v AS SELECT * FROM app.parent;" +
            "DROP TABLE IF EXISTS app.v;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, s.Statements[2].Status);
        Assert.Single(s.FinalCatalog.Views);
        Assert.True(s.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SourceColumnUsedInProvenViewCannotBeDropped()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER, age INTEGER);" +
            "CREATE VIEW app.v_age AS SELECT age FROM app.parent;" +
            "ALTER TABLE app.parent DROP COLUMN age;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, r.Statements[2].Status);
        Assert.Empty(r.Statements[2].Changes);
        Assert.Equal(2, Assert.Single(r.FinalCatalog.Tables).Columns.Count);
        Assert.Single(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnsupportedViewSelectDoesNotMintKnownDependencies()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE VIEW app.v AS SELECT id FROM app.parent WHERE id > 1;");
        var ddl = r.Statements[1];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, ddl.Status);
        Assert.NotEqual(SqlCatalogKnowledge.Known, ddl.Certainty);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void NoViewMayBeInventedFromMissingSourceOrOpenWorld()
    {
        var missing = Analyze("CREATE VIEW app.v AS SELECT * FROM app.missing;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(missing.Statements).Status);
        Assert.Empty(missing.FinalCatalog.Views);
        var unknown = Analyze("CREATE VIEW app.v AS SELECT * FROM app.parent;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlCatalogKnowledge.Known, Assert.Single(unknown.Statements).Certainty);
        Assert.False(unknown.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnsupportedViewQueryMustNotBeAvailableToLaterCascade()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE VIEW app.v AS SELECT id FROM app.parent WHERE id > 1;" +
            "DROP TABLE app.parent CASCADE;");
        Assert.False(r.FinalCatalog.IsClosedWorld);
        Assert.NotEqual(SqlCatalogKnowledge.Known, r.Statements[2].Certainty);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ViewProofIsNeverBorrowedFromPostgresByOtherProviders(SqlAgentToolType provider)
    {
        var r = Analyze("CREATE VIEW app.v AS SELECT * FROM app.parent;", dialect: provider);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(r.Statements).Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
