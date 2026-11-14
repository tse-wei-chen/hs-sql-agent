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
/// A prefix-shared FK group can use one InnoDB physical support index.
/// These tests distinguish *FK dependencies* from *possibly existing indexes*;
/// no FK symbol may be mistaken for a proven independent physical key.
/// </summary>
public sealed class SqlCoreMySqlSharedFkIndexGraphTests
{
    private const string Shared =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
        "CONSTRAINT uq_pair UNIQUE(id,code));" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, note INT, " +
        "CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id), " +
        "CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code));";

    private const string Mixed =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
        "CONSTRAINT uq_pair UNIQUE(id,code));" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, r INT, note INT, " +
        "CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id), " +
        "CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code), " +
        "CONSTRAINT fk_independent FOREIGN KEY(r) REFERENCES parent(id));";

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
    [InlineData("CREATE INDEX ix_note ON child(note)")]
    [InlineData("ALTER TABLE child ADD KEY ix_note(note)")]
    [InlineData("ALTER TABLE child ADD INDEX ix_note(note)")]
    public void SharedFkGroupCanProveUnrelatedSecondaryIndex(string command)
    {
        var result = Analyze(Shared + command + ";");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var child = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("ix_note", Assert.Single(child.Indexes).Name);
        Assert.Equal(new[] { "fk_short", "fk_long" }, child.Constraints
            .Where(c => c.Kind == SqlCatalogConstraintKind.ForeignKey)
            .Select(c => c.Name).ToArray());
        Assert.Empty(result.Statements[1].CatalogAfter.Tables
            .Single(t => t.Name == "child").Indexes);
    }

    [Theory]
    [InlineData("CREATE INDEX ix_shared ON child(p,q,note)")]
    [InlineData("ALTER TABLE child ADD KEY ix_shared(p,q,note)")]
    [InlineData("ALTER TABLE child ADD INDEX ix_shared(p,q)")]
    public void FullSharedGroupCoverHasOneKnownExplicitIndexAndTwoUncertainFkNames(string add)
    {
        var result = Analyze(Shared + add + ";");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var next = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("ix_shared", Assert.Single(next.Indexes).Name);
        Assert.Equal(2, next.Constraints.Count(c =>
            c.Kind == SqlCatalogConstraintKind.ForeignKey));
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(result.Statements[2].Changes).Certainty);
    }

    [Theory]
    [InlineData("DROP INDEX ix_shared ON child")]
    [InlineData("ALTER TABLE child DROP INDEX ix_shared")]
    public void SharedGroupNeedsAlternativeSupportForEveryFkBeforeDrop(string remove)
    {
        var result = Analyze(Shared +
            "CREATE INDEX ix_shared ON child(p,q,note);" +
            "CREATE INDEX ix_backup ON child(p,q);" +
            remove + ";");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal("ix_backup", Assert.Single(result.FinalCatalog.Tables
            .Single(t => t.Name == "child").Indexes).Name);
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Equal(2, result.FinalCatalog.Tables.Single(t => t.Name == "child")
            .Constraints.Count(c => c.Kind == SqlCatalogConstraintKind.ForeignKey));
    }

    [Fact]
    public void CoveringOnlyShorterFkDoesNotProveEntireSharedPhysicalGroup()
    {
        var result = Analyze(Shared + "CREATE INDEX ix_partial ON child(p);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("CREATE INDEX fk_short ON child(p,q)")]
    [InlineData("ALTER TABLE child ADD INDEX fk_long(p,q)")]
    [InlineData("CREATE INDEX FK_LONG ON child(p,q)")]
    public void CannotPromoteAmbiguousImplicitPhysicalNameInSharedGroup(string add)
    {
        var result = Analyze(Shared + add + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void AfterSharedCoverNeitherOriginalImplicitNameCanBeReused()
    {
        var r = Analyze(Shared +
            "CREATE INDEX ix_shared ON child(p,q,note);" +
            "CREATE INDEX FK_SHORT ON child(note);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.DoesNotContain(r.Statements[3].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void AnotherNewDistinctNameCanFollowSharedGroupReplacement()
    {
        var r = Analyze(Shared +
            "CREATE INDEX ix_shared ON child(p,q,note);" +
            "CREATE INDEX ix_note ON child(note);");
        Assert.Empty(r.Diagnostics);
        Assert.Equal(new[] { "ix_shared", "ix_note" }, r.FinalCatalog.Tables
            .Single(t => t.Name == "child").Indexes.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void CannotDropSharedCoverWhenAlternativeSupportsOnlyShorterFk()
    {
        var r = Analyze(Shared +
            "CREATE INDEX ix_shared ON child(p,q,note);" +
            "CREATE INDEX ix_short_only ON child(p);" +
            "DROP INDEX ix_shared ON child;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[4].Status);
        Assert.DoesNotContain(r.Statements[4].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void AfterSafeDropBackupCannotBeDroppedWithoutAnotherFullSupporter()
    {
        var r = Analyze(Shared +
            "CREATE INDEX ix_shared ON child(p,q);" +
            "CREATE INDEX ix_backup ON child(p,q);" +
            "DROP INDEX ix_shared ON child;" +
            "DROP INDEX ix_backup ON child;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[4].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[5].Status);
        Assert.Equal("ix_backup", Assert.Single(r.Statements[4].CatalogAfter.Tables
            .Single(t => t.Name == "child").Indexes).Name);
    }

    [Fact]
    public void SharedGroupCanCoexistWithDisjointThirdFkButNamesRemainIndependent()
    {
        var r = Analyze(Mixed +
            "CREATE INDEX ix_shared ON child(p,q);" +
            "CREATE INDEX ix_third ON child(r);" +
            "CREATE INDEX ix_unrelated ON child(note);");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(new[] { "ix_shared", "ix_third", "ix_unrelated" },
            r.FinalCatalog.Tables.Single(t => t.Name == "child")
                .Indexes.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void UnknownMySqlPhysicalIdentifierSemanticsCannotProveSharedGraph()
    {
        var r = Analyze(Shared + "CREATE INDEX ix_shared ON child(p,q);",
            new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void PostgreSqlMustNotInheritSharedInnoDbPhysicalIndexGraphProof()
    {
        var r = SqlCoreFacade.AnalyzeScript(Shared +
            "CREATE INDEX ix_shared ON child(p,q);",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }
}
