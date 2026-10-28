using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL CREATE OR REPLACE VIEW static proof: its existing visible
/// column contract is immutable except for trailing additions, while direct
/// physical source-column dependencies may change. Not an execution permit.
/// </summary>
public sealed class SqlCorePostgresReplaceViewProofTests
{
    private const string Tables =
        "CREATE TABLE app.old_source(id INTEGER, note TEXT);" +
        "CREATE TABLE app.new_source(id INTEGER, note TEXT, extra INTEGER);";
    private const string Original =
        "CREATE VIEW app.v AS SELECT id AS exposed FROM app.old_source;";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Fact]
    public void ReplacingViewPreservesOutputAndRebindsExactSourceDependencies()
    {
        var result = Analyze(Tables + Original +
            "CREATE VIEW app.child AS SELECT exposed AS child_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT id AS exposed FROM app.new_source;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        var replaced = result.Statements[4];
        Assert.Equal(SqlCatalogOperation.ReplaceView, Assert.Single(replaced.Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.ReplaceView,
            Assert.Single(replaced.DetailedEffects).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known, replaced.Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, replaced.EffectCoverage);
        var view = result.FinalCatalog.Views.Single(x => x.Name == "app.v");
        Assert.Equal("exposed", Assert.Single(view.Columns).Name);
        Assert.Equal("id", Assert.Single(view.SourceColumns));
        Assert.Equal("app.new_source", Assert.Single(view.Dependencies).DisplayName);
        Assert.Equal("app.v", Assert.Single(result.FinalCatalog.Views
            .Single(x => x.Name == "app.child").Dependencies).DisplayName);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OldSourceColumnIsNoLongerBlockedAndNewSourceColumnIsProtected()
    {
        var updated = Tables + Original +
            "CREATE OR REPLACE VIEW app.v AS SELECT id AS exposed FROM app.new_source;";
        var oldDrop = Analyze(updated + "ALTER TABLE app.old_source DROP COLUMN id;");
        Assert.Empty(oldDrop.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, oldDrop.Statements[4].Status);
        Assert.Equal("note", Assert.Single(oldDrop.FinalCatalog.Tables
            .Single(x => x.Name == "app.old_source").Columns).Name);

        var newDrop = Analyze(updated + "ALTER TABLE app.new_source DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, newDrop.Statements[4].Status);
        Assert.Empty(newDrop.Statements[4].Changes);
        Assert.Same(newDrop.Statements[4].CatalogBefore, newDrop.Statements[4].CatalogAfter);
        Assert.Equal(3, newDrop.FinalCatalog.Tables
            .Single(x => x.Name == "app.new_source").Columns.Count);
    }

    [Fact]
    public void AppendingTrailingColumnsKeepsExistingDependentViewUnchanged()
    {
        var result = Analyze(Tables + Original +
            "CREATE VIEW app.child AS SELECT exposed AS child_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT id AS exposed, note FROM app.old_source;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Equal(new[] { "exposed", "note" }, result.FinalCatalog.Views
            .Single(x => x.Name == "app.v").Columns.Select(x => x.Name));
        Assert.Equal("child_id", Assert.Single(result.FinalCatalog.Views
            .Single(x => x.Name == "app.child").Columns).Name);
    }

    [Fact]
    public void ReplacingMissingViewCreatesNewViewNotReplaceEffect()
    {
        var result = Analyze(Tables +
            "CREATE OR REPLACE VIEW app.v AS SELECT id AS exposed FROM app.old_source;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.CreateView,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.CreateView,
            Assert.Single(result.Statements[2].DetailedEffects).Operation);
        Assert.Single(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("SELECT id AS renamed FROM app.old_source")]
    [InlineData("SELECT note AS exposed FROM app.old_source")]
    [InlineData("SELECT note AS exposed, id FROM app.old_source")]
    [InlineData("SELECT id AS exposed, id AS exposed FROM app.old_source")]
    public void IncompatibleReplacementNeverPublishesPartialKnownCatalog(string select)
    {
        var result = Analyze(Tables + Original +
            "CREATE OR REPLACE VIEW app.v AS " + select + ";");
        var replacement = result.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, replacement.Status);
        Assert.Empty(replacement.Changes);
        Assert.Same(replacement.CatalogBefore, replacement.CatalogAfter);
        var existing = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal("app.old_source", Assert.Single(existing.Dependencies).DisplayName);
        Assert.Equal("exposed", Assert.Single(existing.Columns).Name);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ReplacementMayNotRemovePreviouslyPublishedTrailingColumns()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id AS exposed, note FROM app.old_source;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT id AS exposed FROM app.new_source;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Equal(2, Assert.Single(result.FinalCatalog.Views).Columns.Count);
    }

    [Fact]
    public void ReplacementMustNotFormSelfOrTransitiveDependencyCycle()
    {
        var self = Analyze(Tables + Original +
            "CREATE OR REPLACE VIEW app.v AS SELECT exposed FROM app.v;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, self.Statements[3].Status);
        Assert.Empty(self.Statements[3].Changes);
        Assert.Equal("app.old_source",
            Assert.Single(Assert.Single(self.FinalCatalog.Views).Dependencies).DisplayName);

        var transitive = Analyze(Tables + Original +
            "CREATE VIEW app.child AS SELECT exposed FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT exposed FROM app.child;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, transitive.Statements[4].Status);
        Assert.Empty(transitive.Statements[4].Changes);
        Assert.Equal(2, transitive.FinalCatalog.Views.Count);
        Assert.Equal("app.old_source", Assert.Single(transitive.FinalCatalog.Views
            .Single(x => x.Name == "app.v").Dependencies).DisplayName);
    }

    [Fact]
    public void ReplacementOfKnownTableOrIndexCannotBecomeAView()
    {
        var table = Analyze(Tables +
            "CREATE OR REPLACE VIEW app.old_source AS SELECT id FROM app.new_source;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, table.Statements[2].Status);
        Assert.Empty(table.FinalCatalog.Views);
        var index = Analyze(Tables +
            "CREATE INDEX ix_view ON app.new_source(id);" +
            "CREATE OR REPLACE VIEW app.ix_view AS SELECT id FROM app.old_source;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, index.Statements[3].Status);
        Assert.Empty(index.FinalCatalog.Views);
    }

    [Fact]
    public void PlainCreateViewStillRejectsExistingName()
    {
        var result = Analyze(Tables + Original +
            "CREATE VIEW app.v AS SELECT id AS exposed FROM app.new_source;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
    }

    [Fact]
    public void OpenWorldCannotClaimSuccessfulCreateOrReplace()
    {
        var result = Analyze(
            "CREATE OR REPLACE VIEW app.v AS SELECT id FROM app.orders;",
            SqlCatalogState.Unknown());
        var statement = Assert.Single(result.Statements);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.NotEqual(SqlCatalogKnowledge.Known, statement.Certainty);
        Assert.DoesNotContain(statement.Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("SELECT id + 1 AS exposed FROM app.old_source")]
    [InlineData("SELECT id AS exposed FROM app.old_source WHERE id IN (1, 2)")]
    [InlineData("SELECT id AS exposed FROM app.old_source LEFT JOIN app.new_source USING (id) JOIN app.old_source x USING (id)")]
    public void UnmodeledReplacementBodiesDoNotMintKnownCatalog(string select)
    {
        var result = Analyze(Tables + Original +
            "CREATE OR REPLACE VIEW app.v AS " + select + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[3].Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsDoNotInheritPostgresReplacementProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE OR REPLACE VIEW app.v AS SELECT id FROM app.orders;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }
}
