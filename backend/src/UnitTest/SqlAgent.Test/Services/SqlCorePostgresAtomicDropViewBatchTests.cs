using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A multi-view DROP is a single immutable Catalog proof. Dependencies on
/// other members of the same batch satisfy RESTRICT, but external dependents
/// require CASCADE. No partial Known effects on any failed member.
/// </summary>
public sealed class SqlCorePostgresAtomicDropViewBatchTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null, SqlAgentToolType provider = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, initial ?? SqlCatalogState.KnownEmpty());

    private const string Chain =
        "CREATE TABLE app.parent(id INTEGER, note TEXT);" +
        "CREATE VIEW app.v_parent AS SELECT * FROM app.parent;" +
        "CREATE VIEW app.v_child AS SELECT id FROM app.v_parent;" +
        "CREATE VIEW app.v_grandchild AS SELECT * FROM app.v_child;";

    [Fact]
    public void TwoIndependentViewsDropAsOneKnownBatch()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE VIEW app.a AS SELECT * FROM app.parent;" +
            "CREATE VIEW audit.b AS SELECT * FROM app.parent;" +
            "DROP VIEW app.a, audit.b RESTRICT;");

        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, drop.Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, drop.EffectCoverage);
        Assert.Equal(new[] { "app.a", "audit.b" },
            drop.Changes.Select(x => x.TableName).ToArray());
        Assert.All(drop.Changes, x =>
        {
            Assert.Equal(SqlCatalogOperation.DropView, x.Operation);
            Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty);
        });
        Assert.Equal(2, drop.DetailedEffects.Count);
        Assert.All(drop.DetailedEffects,
            x => Assert.Equal(SqlStatementEffectOperation.DropView, x.Operation));
        Assert.Single(r.FinalCatalog.Tables);
        Assert.Empty(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP VIEW app.v_child, app.v_parent RESTRICT;")]
    [InlineData("DROP VIEW app.v_parent, app.v_child RESTRICT;")]
    public void RestrictedBatchAllowsIntraBatchViewDependency(string statement)
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE VIEW app.v_parent AS SELECT * FROM app.parent;" +
            "CREATE VIEW app.v_child AS SELECT * FROM app.v_parent;" +
            statement);
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(2, drop.Changes.Count);
        Assert.Empty(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void RestrictBlocksRemainingTransitiveViewAndPublishesNoChanges()
    {
        var r = Analyze(Chain + "DROP VIEW app.v_parent, app.v_child RESTRICT;");
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Contains(drop.Diagnostics, d => d.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Equal(3, r.FinalCatalog.Views.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void CascadeDeduplicatesNamedAndTransitiveDependents()
    {
        var r = Analyze(Chain + "DROP VIEW app.v_parent, app.v_child CASCADE;");
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(3, drop.Changes.Count);
        Assert.Equal(3, drop.DetailedEffects.Count);
        Assert.Equal(3, drop.Changes.Select(x => x.TableIdentity!.Key).Distinct().Count());
        Assert.All(drop.Changes, x =>
            Assert.Equal(SqlCatalogOperation.DropView, x.Operation));
        Assert.Empty(r.FinalCatalog.Views);
        Assert.Single(r.FinalCatalog.Tables);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MissingLaterViewMustNotDropEarlierValidView()
    {
        var r = Analyze(Chain + "DROP VIEW app.v_parent, app.missing CASCADE;");
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Equal(3, r.FinalCatalog.Views.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalBatchSkipsKnownAbsentAndDropsKnownView()
    {
        var r = Analyze(Chain + "DROP VIEW IF EXISTS app.unknown, app.v_grandchild RESTRICT;");
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal("app.v_grandchild", Assert.Single(drop.Changes).TableName);
        Assert.Equal(2, r.FinalCatalog.Views.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AllAbsentConditionalBatchIsExactNoOp()
    {
        var r = Analyze("DROP VIEW IF EXISTS app.absent, audit.absent CASCADE;");
        var drop = Assert.Single(r.Statements);
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, drop.Certainty);
        Assert.Empty(drop.Changes);
        Assert.Empty(drop.DetailedEffects);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DuplicateRequestedIdentityRejectsWholeBatch()
    {
        var r = Analyze(Chain + "DROP VIEW app.v_parent, app.v_parent CASCADE;");
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Equal(3, r.FinalCatalog.Views.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void BatchCannotTreatKnownTableAsAnAbsentView()
    {
        var r = Analyze(Chain + "DROP VIEW IF EXISTS app.v_parent, app.parent CASCADE;");
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Equal(3, r.FinalCatalog.Views.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void BatchCannotTreatKnownIndexAsAnAbsentView()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE INDEX ix_id ON app.parent(id);" +
            "CREATE VIEW app.v AS SELECT id FROM app.parent;" +
            "DROP VIEW IF EXISTS app.v, app.ix_id CASCADE;");
        var drop = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Single(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AmbiguousImplicitKeyIndexCannotProveMissingViewIdentity()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE VIEW app.v AS SELECT id FROM app.parent;" +
            "DROP VIEW IF EXISTS app.v, app.unknown_index CASCADE;");
        var drop = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, drop.Certainty);
        Assert.DoesNotContain(drop.Changes, x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ProvenSearchPathSupportsMixedQualifiedAndUnqualifiedBatch()
    {
        var r = Analyze(Chain +
            "SET search_path TO app;" +
            "DROP VIEW v_child, app.v_grandchild RESTRICT;");
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[5];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(2, drop.Changes.Count);
        Assert.Single(r.FinalCatalog.Views);
        Assert.Equal("app.v_parent", Assert.Single(r.FinalCatalog.Views).Name);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void WithoutProvenSearchPathUnqualifiedViewMustNotGuess()
    {
        var r = Analyze(Chain + "DROP VIEW v_child, app.v_grandchild CASCADE;");
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Empty(drop.Changes.Where(x => x.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void QuotedDottedNameDoesNotEqualSchemaQualifiedName()
    {
        var r = Analyze(
            "SET search_path TO app;" +
            "CREATE TABLE app.parent(id INTEGER);" +
            "CREATE VIEW app.v AS SELECT id FROM app.parent;" +
            "CREATE VIEW \"app.v\" AS SELECT id FROM app.parent;" +
            "DROP VIEW app.v, \"app.v\" RESTRICT;");
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(2, drop.Changes.Count);
        Assert.Empty(r.FinalCatalog.Views);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnknownCatalogCannotMintBatchOrConditionalNoOp()
    {
        var r = Analyze("DROP VIEW IF EXISTS app.a, app.b CASCADE;",
            SqlCatalogState.Unknown());
        var drop = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, drop.Certainty);
        Assert.Empty(drop.Changes.Where(x => x.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void MultiViewGrammarIsPostgreSqlOnly(SqlAgentToolType dialect)
    {
        var r = Analyze("DROP VIEW app.a, app.b CASCADE;", provider: dialect);
        var drop = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP VIEW app.a, app.b CASCADE RESTRICT;")]
    [InlineData("DROP VIEW app.a, app.b CASCADE CASCADE;")]
    [InlineData("DROP VIEW app.a, app.b ON app.a;")]
    [InlineData("DROP VIEW CONCURRENTLY app.a, app.b;")]
    public void UnsupportedModifiersRemainFailClosed(string sql)
    {
        var r = Analyze(sql);
        var drop = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
