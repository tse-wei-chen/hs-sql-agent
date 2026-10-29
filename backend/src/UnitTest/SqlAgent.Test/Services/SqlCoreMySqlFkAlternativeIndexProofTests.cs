using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// The implicit InnoDB FK support index may disappear after CREATE INDEX.
/// A later distinct physical index name is provably free only when ALL
/// possibly retained implicit index names are carried through Catalog
/// history, and a DROP requires independent explicit supporting evidence.
/// </summary>
public sealed class SqlCoreMySqlFkAlternativeIndexProofTests
{
    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });
    private const string Graph =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, note INT, " +
        "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), profile ?? Linux,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("CREATE INDEX ix_backup ON child(pid)")]
    [InlineData("ALTER TABLE child ADD KEY ix_backup(pid)")]
    [InlineData("ALTER TABLE child ADD INDEX ix_backup(pid,note)")]
    public void AlternativeSupportCanBeProvenAfterPossibleImplicitReplacement(string add)
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            add + ";");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var before = r.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child");
        var after = r.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("ix_cover", Assert.Single(before.Indexes).Name);
        Assert.Equal(new[] { "ix_cover", "ix_backup" },
            after.Indexes.Select(x => x.Name).ToArray());
        Assert.Equal("fk_child", after.Constraints.Single(x =>
            x.Kind == SqlCatalogConstraintKind.ForeignKey).Name);
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(r.Statements[3].Changes).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(r.Statements[3].Changes).Certainty);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("ALTER TABLE child DROP INDEX ix_cover")]
    [InlineData("DROP INDEX ix_cover ON child")]
    [InlineData("ALTER TABLE child DROP KEY IX_COVER")]
    public void DropOldCoveringIndexIsProvenOnlyAfterIndependentAlternative(string remove)
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "CREATE INDEX ix_backup ON child(pid);" +
            remove + ";");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var after = r.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("ix_backup", Assert.Single(after.Indexes).Name);
        Assert.Single(after.Constraints.Where(x =>
            x.Kind == SqlCatalogConstraintKind.ForeignKey));
        Assert.Equal(2, r.Statements[3].CatalogAfter.Tables
            .Single(t => t.Name == "child").Indexes.Count);
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(r.Statements[4].Changes).Operation);
    }

    [Fact]
    public void CannotDropFinalExplicitFkSupportAfterDroppingThePreviousOne()
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "CREATE INDEX ix_backup ON child(pid);" +
            "DROP INDEX ix_cover ON child;" +
            "DROP INDEX ix_backup ON child;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[4].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[5].Status);
        Assert.DoesNotContain(r.Statements[5].Changes,
            d => d.Certainty == SqlCatalogKnowledge.Known);
        Assert.Equal("ix_backup", Assert.Single(r.Statements[4].CatalogAfter.Tables
            .Single(t => t.Name == "child").Indexes).Name);
    }

    [Theory]
    [InlineData("CREATE INDEX fk_child ON child(note)")]
    [InlineData("CREATE INDEX FK_CHILD ON child(note)")]
    public void MayRetainIndexNameIsNeverAssumedAbsent(string add)
    {
        var r = Analyze(Graph + "CREATE INDEX ix_cover ON child(pid,note);" + add + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.DoesNotContain(r.Statements[3].Changes,
            d => d.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void AfterDroppingFkKnownResidualNameDoesNotBlockDistinctNames()
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "ALTER TABLE child DROP FOREIGN KEY fk_child;" +
            "CREATE INDEX ix_new ON child(note);");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var after = r.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal(new[] { "ix_cover", "ix_new" },
            after.Indexes.Select(x => x.Name).ToArray());
        Assert.DoesNotContain(after.Constraints,
            x => x.Kind == SqlCatalogConstraintKind.ForeignKey);
    }

    [Fact]
    public void AfterDroppingFkPossiblePhysicalNameCollisionIsStillRejected()
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "ALTER TABLE child DROP FOREIGN KEY fk_child;" +
            "CREATE INDEX fk_child ON child(note);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[4].Status);
        Assert.DoesNotContain(r.Statements[4].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void SameNamePromotionThenAlternativeRetainsConservativeDropSafety()
    {
        var r = Analyze(Graph +
            "CREATE INDEX fk_child ON child(pid,note);" +
            "CREATE INDEX ix_backup ON child(pid);" +
            "DROP INDEX fk_child ON child;");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        Assert.Equal("ix_backup", Assert.Single(
            r.FinalCatalog.Tables.Single(t => t.Name == "child").Indexes).Name);
    }

    [Fact]
    public void UnknownMySqlIdentifierModeDoesNotObtainResidualNameProof()
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "CREATE INDEX ix_backup ON child(pid);",
            new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[^1].Status);
        Assert.DoesNotContain(r.Statements[^1].Changes,
            d => d.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void NonMySqlProviderCannotInheritResidualNameEvidence()
    {
        var r = SqlCoreFacade.AnalyzeScript(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "CREATE INDEX ix_backup ON child(pid);",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes,
            d => d.Certainty == SqlCatalogKnowledge.Known);
    }
}
