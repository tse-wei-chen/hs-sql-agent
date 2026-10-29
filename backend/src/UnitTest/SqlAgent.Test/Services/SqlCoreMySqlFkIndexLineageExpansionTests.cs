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
/// Test-driven MySQL 8 InnoDB FK-supporting index physical lineage:
/// same-name covering-index promotion and independently owned multiple FKs.
/// No guessed implicit-index deletion may appear in the known Catalog.
/// </summary>
public sealed class SqlCoreMySqlFkIndexLineageExpansionTests
{
    private const string OneFk =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, note INT, " +
        "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));";

    private const string TwoIndependentFks =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p1 INT, p2 INT, note INT, " +
        "CONSTRAINT fk_one FOREIGN KEY(p1) REFERENCES parent(id), " +
        "CONSTRAINT fk_two FOREIGN KEY(p2) REFERENCES parent(id));";

    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), profile ?? Linux,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("CREATE INDEX fk_child ON child(pid,note)")]
    [InlineData("ALTER TABLE child ADD INDEX fk_child(pid,note)")]
    [InlineData("ALTER TABLE child ADD KEY fk_child(pid,note)")]
    public void SameNameCoveringIndexCanBeProvenWithoutClaimingImplicitIndexWasDropped(string add)
    {
        var result = Analyze(OneFk + add + ";");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var before = result.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "child");
        var after = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Empty(before.Indexes);
        Assert.Equal(new[] { "pid", "note" }, Assert.Single(after.Indexes).Columns.ToArray());
        Assert.Equal("fk_child", Assert.Single(after.Indexes).Name);
        Assert.Equal("fk_child", after.Constraints.Single(x =>
            x.Kind == SqlCatalogConstraintKind.ForeignKey).Name);
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(result.Statements[2].Changes).Certainty);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SameNameReplacementCanProveDistinctNewIndexName()
    {
        var result = Analyze(OneFk +
            "CREATE INDEX fk_child ON child(pid,note);" +
            "CREATE INDEX ix_note ON child(note);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(2, result.FinalCatalog.Tables
            .Single(t => t.Name == "child").Indexes.Count);
        Assert.Equal("fk_child", Assert.Single(
            result.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes).Name);
    }

    [Theory]
    [InlineData("ALTER TABLE child DROP KEY fk_child;")]
    [InlineData("DROP INDEX fk_child ON child;")]
    public void SameNameCoveringIndexCannotBeDroppedIfItMightBeLastFkSupport(string removal)
    {
        var result = Analyze(OneFk +
            "CREATE INDEX fk_child ON child(pid,note);" + removal);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void SameNameIndexPromotionThenDropFkKeepsResidualPhysicalHistory()
    {
        var result = Analyze(OneFk +
            "CREATE INDEX fk_child ON child(pid,note);" +
            "ALTER TABLE child DROP FOREIGN KEY fk_child;" +
            "CREATE INDEX ix_extra ON child(note);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Single(result.Statements[3].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes);
    }

    [Theory]
    [InlineData("CREATE INDEX ix_note ON child(note)")]
    [InlineData("ALTER TABLE child ADD INDEX ix_note(note)")]
    public void TwoIndependentNamedFksAllowUnrelatedSecondaryIndex(string add)
    {
        var result = Analyze(TwoIndependentFks + add + ";");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var table = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal(new[] { "fk_one", "fk_two" }, table.Constraints
            .Where(c => c.Kind == SqlCatalogConstraintKind.ForeignKey)
            .Select(c => c.Name).ToArray());
        Assert.Equal("ix_note", Assert.Single(table.Indexes).Name);
        Assert.Empty(result.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes);
    }

    [Fact]
    public void TwoIndependentNamedFksPermitOnePrefixCoveringReplacement()
    {
        var result = Analyze(TwoIndependentFks +
            "CREATE INDEX ix_p1 ON child(p1,note);" +
            "CREATE INDEX ix_p2 ON child(p2,note);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        // The two FK physical names are independent and known; neither
        // implicit index is presumed absent, but the second *new* name is.
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(2, result.FinalCatalog.Tables
            .Single(t => t.Name == "child").Indexes.Count);
        Assert.Equal("ix_p1", Assert.Single(result.Statements[2]
            .CatalogAfter.Tables.Single(t => t.Name == "child").Indexes).Name);
    }

    [Fact]
    public void TwoIndependentNamedFksAllowSameNamePromotionOfExactlyOne()
    {
        var result = Analyze(TwoIndependentFks +
            "CREATE INDEX fk_one ON child(p1,note);");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var table = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("fk_one", Assert.Single(table.Indexes).Name);
        Assert.Equal(2, table.Constraints.Count(c =>
            c.Kind == SqlCatalogConstraintKind.ForeignKey));
    }

    [Theory]
    [InlineData("CREATE INDEX fk_child ON child(note)")]
    [InlineData("ALTER TABLE child ADD KEY fk_child(note)")]
    public void SameNameWithoutCoveringFkColumnsRemainsFailClosed(string add)
    {
        var result = Analyze(OneFk + add + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void SharedFkPrefixGroupAllowsUnrelatedIndexButStillKnowsBothConstraints()
    {
        const string sql =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, second_id INT NOT NULL, " +
            "CONSTRAINT uq_parent_pair UNIQUE(id,second_id));" +
            "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p1 INT, p2 INT, note INT, " +
            "CONSTRAINT fk_one FOREIGN KEY(p1) REFERENCES parent(id), " +
            "CONSTRAINT fk_two FOREIGN KEY(p1,p2) REFERENCES parent(id,second_id));" +
            "CREATE INDEX ix_note ON child(note);";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var child = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("ix_note", Assert.Single(child.Indexes).Name);
        Assert.Equal(2, child.Constraints.Count(c =>
            c.Kind == SqlCatalogConstraintKind.ForeignKey));
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(result.Statements[^1].Changes).Operation);
    }

    [Fact]
    public void UnknownProviderIdentityCannotProveSameNameReplacement()
    {
        var result = Analyze(OneFk +
            "CREATE INDEX fk_child ON child(pid,note);",
            new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void PostgreSqlDoesNotInheritMySqlSameNamePromotion()
    {
        var result = SqlCoreFacade.AnalyzeScript(OneFk +
            "CREATE INDEX fk_child ON child(pid,note);",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }
}
