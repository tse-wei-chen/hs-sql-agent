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
/// A named InnoDB FK may own an implicit supporting index. Only a
/// non-covering named secondary index can be added without changing that
/// supporting index; after DROP FOREIGN KEY its physical fate remains opaque.
/// </summary>
public sealed class SqlCoreMySqlForeignKeySupportingIndexStabilityTests
{
    private const string Graph =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, note INT, " +
        "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));";

    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            catalog ?? SqlCatalogState.KnownEmpty(),
            profile ?? Linux, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("CREATE INDEX ix_note ON child(note)")]
    [InlineData("ALTER TABLE child ADD KEY ix_note(note)")]
    [InlineData("ALTER TABLE child ADD INDEX ix_note(note)")]
    public void DisjointIndexAddPreservesNamedForeignKeyAndImmutableSnapshots(string statement)
    {
        var r = Analyze(Graph + statement + ";");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var before = r.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "child");
        var after = r.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("fk_child", after.Constraints.Single(c =>
            c.Kind == SqlCatalogConstraintKind.ForeignKey).Name);
        Assert.Equal("ix_note", Assert.Single(after.Indexes).Name);
        Assert.Empty(before.Indexes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
        var delta = Assert.Single(r.Statements[2].Changes);
        Assert.Equal(SqlCatalogOperation.CreateIndex, delta.Operation);
        Assert.Equal(SqlCatalogKnowledge.Known, delta.Certainty);
        Assert.Equal("child", delta.TableIdentity!.DisplayName);
    }

    [Fact]
    public void NonCoveringIndexCanRoundTripAndForeignKeyRemainsBound()
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_note ON child(note);" +
            "ALTER TABLE child DROP KEY ix_note;");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Empty(r.FinalCatalog.Tables.Single(t => t.Name == "child").Indexes);
        Assert.Equal("ix_note", Assert.Single(
            r.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes).Name);
        Assert.Equal(SqlCatalogConstraintKind.ForeignKey, r.FinalCatalog.Tables
            .Single(t => t.Name == "child").Constraints.Last().Kind);
    }

    [Theory]
    [InlineData("CREATE INDEX ix_cover ON child(pid)")]
    [InlineData("CREATE INDEX ix_cover ON child(pid,note)")]
    [InlineData("ALTER TABLE child ADD KEY ix_cover(pid,note)")]
    public void IndexCoveringForeignKeyPrefixTracksProvenIndexAndUnprovenImplicitReplacement(string statement)
    {
        var r = Analyze(Graph + statement + ";" +
            "CREATE INDEX ix_after ON child(note);");
        var step = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.Equal(SqlCatalogOperation.CreateIndex, Assert.Single(step.Changes).Operation);
        Assert.Equal("ix_cover", Assert.Single(
            step.CatalogAfter.Tables.Single(t => t.Name == "child").Indexes).Name);
        Assert.Empty(r.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes);
        // The old auto-FK index may survive, but ix_after is a distinct
        // physical name and is provably safe.
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.Equal(2, r.FinalCatalog.Tables
            .Single(t => t.Name == "child").Indexes.Count);
    }

    [Theory]
    [InlineData("CREATE INDEX fk_child ON child(note)")]
    [InlineData("CREATE INDEX FK_CHILD ON child(note)")]
    [InlineData("ALTER TABLE child ADD KEY fk_child(note)")]
    public void ImplicitForeignKeySupportingIndexNameCannotBeReused(string statement)
    {
        var r = Analyze(Graph + statement + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("CREATE INDEX ix_after ON child(note)")]
    [InlineData("CREATE INDEX fk_child ON child(note)")]
    public void DropForeignKeyDoesNotProveSupportingIndexWasDropped(string statement)
    {
        var r = Analyze(Graph +
            "ALTER TABLE child DROP FOREIGN KEY fk_child;" +
            statement + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.Equal(SqlCatalogOperation.DropConstraint,
            Assert.Single(r.Statements[2].Changes).Operation);
        if (statement.Contains("ix_after", StringComparison.Ordinal))
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        else
        {
            Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
            Assert.DoesNotContain(r.Statements[3].Changes,
                x => x.Certainty == SqlCatalogKnowledge.Known);
        }
        Assert.True(r.Statements[2].CatalogAfter.IsClosedWorld);
    }

    [Fact]
    public void DropForeignKeyAfterDisjointIndexDoesNotEraseIndexHistory()
    {
        var r = Analyze(Graph +
            "CREATE INDEX ix_note ON child(note);" +
            "ALTER TABLE child DROP FOREIGN KEY fk_child;" +
            "CREATE INDEX ix_other ON child(id);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        var after = r.Statements[3].CatalogAfter.Tables.Single(t => t.Name == "child");
        Assert.Equal("ix_note", Assert.Single(after.Indexes).Name);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[4].Status);
        Assert.Equal(2, r.FinalCatalog.Tables
            .Single(t => t.Name == "child").Indexes.Count);
    }

    [Fact]
    public void MissingDeclaredMySqlPhysicalNameModeNeverProvesForeignKeyIndexAddition()
    {
        var r = Analyze(Graph + "CREATE INDEX ix_note ON child(note);",
            profile: new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnnamedForeignKeyIndexNeverGainsInferredName()
    {
        // Both index and generated FK symbol are unmodeled.
        var r = Analyze(
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(pid INT, note INT, FOREIGN KEY(pid) REFERENCES parent(id));" +
            "CREATE INDEX ix_note ON child(note);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[^1].Status);
        Assert.DoesNotContain(r.Statements[^1].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void PostgreSqlIndexCreationOnForeignKeyOwnerKeepsExistingFailClosedFence()
    {
        var r = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(pid INT, note INT, CONSTRAINT fk_child " +
            "FOREIGN KEY(pid) REFERENCES parent(id));" +
            "CREATE INDEX ix_note ON child(note);",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[^1].Status);
        Assert.DoesNotContain(r.Statements[^1].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }
}
