using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL DROP TABLE CASCADE removes inbound foreign-key constraints on
/// surviving tables, not the surviving tables themselves. Every known delta
/// is virtual/static and must be published only after entire-batch proof.
/// </summary>
public sealed class SqlCorePostgresForeignKeyCascadeTests
{
    private static SqlScriptAnalysisResult Analyze(string script,
        SqlCatalogState? catalog = null, SqlAgentToolType provider = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(script, provider, catalog ?? SqlCatalogState.KnownEmpty());

    private const string ParentAndChild =
        "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
        "CREATE TABLE app.child(id INTEGER, parent_id INTEGER," +
        "CONSTRAINT fk_parent FOREIGN KEY(parent_id) REFERENCES app.parent(id));";

    [Fact]
    public void SingleTableCascadeDropsParentAndOnlyChildForeignKey()
    {
        var r = Analyze(ParentAndChild + "DROP TABLE app.parent CASCADE;");
        Assert.Empty(r.Diagnostics);
        var ddl = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, ddl.Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, ddl.EffectCoverage);
        Assert.Equal(2, ddl.Changes.Count);
        Assert.Equal(SqlCatalogOperation.DropTable, ddl.Changes[0].Operation);
        var fk = ddl.Changes[1];
        Assert.Equal(SqlCatalogOperation.DropConstraint, fk.Operation);
        Assert.Equal("app.child", fk.TableName);
        Assert.Equal("fk_parent", fk.ConstraintName);
        Assert.Equal(SqlCatalogKnowledge.Known, fk.Certainty);
        Assert.Equal(2, ddl.DetailedEffects.Count);
        Assert.Equal(SqlStatementEffectOperation.DropConstraint, ddl.DetailedEffects[1].Operation);
        Assert.Equal("fk_parent", ddl.DetailedEffects[1].ConstraintName);
        Assert.Equal(SqlCatalogKnowledge.Known, ddl.DetailedEffects[1].Certainty);
        var child = Assert.Single(r.FinalCatalog.Tables);
        Assert.Equal("app.child", child.Name);
        Assert.Empty(child.Constraints);
        Assert.Equal(2, child.Columns.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void CascadeRemovesMultipleNamedForeignKeysOnSameSurvivingTable()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE app.other(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE app.child(id INTEGER, parent_id INTEGER, parent2_id INTEGER, other_id INTEGER," +
            "CONSTRAINT fk_one FOREIGN KEY(parent_id) REFERENCES app.parent(id)," +
            "CONSTRAINT fk_two FOREIGN KEY(parent2_id) REFERENCES app.parent(id)," +
            "CONSTRAINT fk_other FOREIGN KEY(other_id) REFERENCES app.other(id));" +
            "DROP TABLE app.parent CASCADE;");
        Assert.Empty(r.Diagnostics);
        var ddl = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Equal(3, ddl.Changes.Count);
        Assert.Equal(new[] { "fk_one", "fk_two" },
            ddl.Changes.Where(x => x.Operation == SqlCatalogOperation.DropConstraint)
                .Select(x => x.ConstraintName).ToArray());
        var child = r.FinalCatalog.Tables.Single(t => t.Name == "app.child");
        Assert.Equal("fk_other", Assert.Single(child.Constraints).Name);
        Assert.Single(r.FinalCatalog.Tables.Single(t => t.Name == "app.other").Constraints);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MultiTableCascadeRemovesExternalFksOnlyAfterWholeBatchProof()
    {
        var r = Analyze(ParentAndChild +
            "CREATE TABLE audit.external(id INTEGER, parent_id INTEGER, child_id INTEGER," +
            "CONSTRAINT fk_parent_ext FOREIGN KEY(parent_id) REFERENCES app.parent(id));" +
            "DROP TABLE app.parent, app.child CASCADE;");
        Assert.Empty(r.Diagnostics);
        var ddl = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Equal(3, ddl.Changes.Count);
        Assert.Equal(2, ddl.Changes.Count(x => x.Operation == SqlCatalogOperation.DropTable));
        Assert.Equal("fk_parent_ext",
            Assert.Single(ddl.Changes.Where(x => x.Operation == SqlCatalogOperation.DropConstraint)).ConstraintName);
        var survivor = Assert.Single(r.FinalCatalog.Tables);
        Assert.Equal("audit.external", survivor.Name);
        Assert.Empty(survivor.Constraints);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void CascadeDoesNotDropFKsToSurvivingTables()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE app.survivor(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE app.child(parent_id INTEGER, survivor_id INTEGER," +
            "CONSTRAINT fk_parent FOREIGN KEY(parent_id) REFERENCES app.parent(id)," +
            "CONSTRAINT fk_survivor FOREIGN KEY(survivor_id) REFERENCES app.survivor(id));" +
            "DROP TABLE app.parent CASCADE;");
        Assert.Empty(r.Diagnostics);
        Assert.Equal("fk_survivor",
            Assert.Single(r.FinalCatalog.Tables.Single(t => t.Name == "app.child").Constraints).Name);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void RestrictStillBlocksForeignKeyFromSurvivingTable()
    {
        var r = Analyze(ParentAndChild + "DROP TABLE app.parent RESTRICT;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, r.Statements[2].Status);
        Assert.Empty(r.Statements[2].Changes);
        Assert.Equal(2, r.FinalCatalog.Tables.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void BatchFailureAfterValidCascadeTargetCannotPublishPartialDeltas()
    {
        var r = Analyze(ParentAndChild +
            "DROP TABLE app.parent, app.missing CASCADE;");
        var ddl = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, ddl.Status);
        Assert.Empty(ddl.Changes);
        Assert.Same(ddl.CatalogBefore, ddl.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
        Assert.Equal(2, r.FinalCatalog.Tables.Count);
    }

    [Fact]
    public void ConditionalCascadeSkipsKnownAbsentButRemovesKnownFK()
    {
        var r = Analyze(ParentAndChild +
            "DROP TABLE IF EXISTS app.missing, app.parent CASCADE;");
        Assert.Empty(r.Diagnostics);
        var ddl = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Equal(2, ddl.Changes.Count);
        Assert.Equal("fk_parent",
            Assert.Single(ddl.Changes.Where(x => x.Operation == SqlCatalogOperation.DropConstraint)).ConstraintName);
        Assert.Single(r.FinalCatalog.Tables);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalCascadeOnClosedKnownEmptyCatalogIsExactNoOp()
    {
        var r = Analyze("DROP TABLE IF EXISTS app.missing CASCADE;");
        var ddl = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, ddl.Certainty);
        Assert.Empty(ddl.Changes);
        Assert.Same(ddl.CatalogBefore, ddl.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnnamedExternalForeignKeyCannotPublishExactConstraintIdentity()
    {
        var r = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE app.child(parent_id INTEGER," +
            "FOREIGN KEY(parent_id) REFERENCES app.parent(id));" +
            "DROP TABLE app.parent CASCADE;");
        var ddl = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, ddl.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, ddl.Certainty);
        Assert.DoesNotContain(ddl.Changes, x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ExplicitDropConstraintAlsoReturnsTheExactTypedConstraintName()
    {
        var r = Analyze(ParentAndChild +
            "ALTER TABLE app.child DROP CONSTRAINT fk_parent;");
        Assert.Empty(r.Diagnostics);
        var ddl = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Equal(SqlCatalogOperation.DropConstraint, Assert.Single(ddl.Changes).Operation);
        Assert.Equal("fk_parent", Assert.Single(ddl.Changes).ConstraintName);
        Assert.Equal("fk_parent", Assert.Single(ddl.DetailedEffects).ConstraintName);
        Assert.Empty(r.FinalCatalog.Tables.Single(t => t.Name == "app.child").Constraints);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OpenWorldDoesNotProveCascadeDependenciesOrAbsence()
    {
        foreach (var sql in new[]
        {
            "DROP TABLE app.parent CASCADE;",
            "DROP TABLE IF EXISTS app.parent CASCADE;",
            "DROP TABLE app.parent, audit.child CASCADE;"
        })
        {
            var r = Analyze(sql, SqlCatalogState.Unknown());
            var ddl = Assert.Single(r.Statements);
            Assert.Equal(SqlStatementAnalysisStatus.Unsupported, ddl.Status);
            Assert.Equal(SqlCatalogKnowledge.Unknown, ddl.Certainty);
            Assert.False(r.FinalCatalog.IsClosedWorld);
            Assert.Empty(ddl.Changes.Where(x => x.Certainty == SqlCatalogKnowledge.Known));
        }
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void PostgresCascadeProofDoesNotLeakToOtherProviders(SqlAgentToolType provider)
    {
        var r = Analyze("DROP TABLE app.parent CASCADE;", provider: provider);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(r.Statements).Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP TABLE app.parent CASCADE RESTRICT;")]
    [InlineData("DROP TABLE app.parent RESTRICT CASCADE;")]
    [InlineData("DROP TABLE app.parent CASCADE CASCADE;")]
    public void ContradictoryOrDuplicatedDropModesAreNotAccepted(string sql)
    {
        var r = Analyze(sql);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(r.Statements).Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
