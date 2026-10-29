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
/// ALTER FOREIGN KEY is an incremental graph change. DROP can recompute a
/// shared-prefix group, but ADD cannot infer existing row integrity from
/// a structural Catalog, even when CREATE TABLE appears earlier in script.
/// </summary>
public sealed class SqlCoreMySqlAlterFkGraphTransitionTests
{
    private const string Shared =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
        "CONSTRAINT uq_pair UNIQUE(id,code));" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, note INT, " +
        "CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id)," +
        "CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code));";
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
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short;", "fk_long", "CREATE INDEX ix_p ON child(p);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_long;", "fk_short", "CREATE INDEX ix_cover ON child(p,q);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, DROP FOREIGN KEY fk_long;", "", "CREATE INDEX ix_note ON child(note);")]
    public void DropIncrementallyRebuildsCurrentFkPrefixDependencies(
        string drop, string remaining, string add)
    {
        var r = Analyze(Shared + drop + add);
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var before = r.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "child");
        var afterDrop = r.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child");
        var afterIndex = r.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal(2, before.Constraints.Count(x => x.Kind == SqlCatalogConstraintKind.ForeignKey));
        var names = afterDrop.Constraints
            .Where(x => x.Kind == SqlCatalogConstraintKind.ForeignKey)
            .Select(x => x.Name).ToArray();
        Assert.Equal(string.IsNullOrEmpty(remaining) ? [] : [remaining], names);
        Assert.Equal(names, afterIndex.Constraints
            .Where(x => x.Kind == SqlCatalogConstraintKind.ForeignKey)
            .Select(x => x.Name).ToArray());
        Assert.Single(afterIndex.Indexes);
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(r.Statements[3].Changes).Operation);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("fk_short")]
    [InlineData("fk_long")]
    public void DroppedForeignKeySymbolMayStillOwnAnImplicitIndexName(string dropped)
    {
        var r = Analyze(Shared +
            $"ALTER TABLE child DROP FOREIGN KEY {dropped};" +
            $"CREATE INDEX {dropped} ON child(note);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.DoesNotContain(r.Statements[3].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void DropSharedFkThenReplaceRemainingWithTwoKnownAlternatives()
    {
        var r = Analyze(Shared +
            "ALTER TABLE child DROP FOREIGN KEY fk_long;" +
            "CREATE INDEX ix_primary_support ON child(p,q);" +
            "CREATE INDEX ix_backup ON child(p);" +
            "DROP INDEX ix_primary_support ON child;");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var after = r.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("fk_short", Assert.Single(after.Constraints
            .Where(x => x.Kind == SqlCatalogConstraintKind.ForeignKey)).Name);
        Assert.Equal("ix_backup", Assert.Single(after.Indexes).Name);
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(r.Statements[^1].Changes).Operation);
    }

    [Theory]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(p) REFERENCES parent(id);")]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(p,q) REFERENCES parent(id,code);")]
    [InlineData("ALTER TABLE child ADD FOREIGN KEY (p) REFERENCES parent(id);")]
    public void AddForeignKeySyntaxIsRecognizedButNoDataIntegrityProofIsInvented(string add)
    {
        const string plain =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
            "CONSTRAINT uq_pair UNIQUE(id,code));" +
            "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, note INT);";
        var r = Analyze(plain + add);
        var step = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, step.Status);
        Assert.Contains(step.Diagnostics, d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(step.Changes, c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(step.CatalogBefore.Tables.Single(t => t.Name == "child").Constraints);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AddForeignKeyEvenAfterDropOfSameSymbolDoesNotAssumeNoChildRows()
    {
        var r = Analyze(Shared +
            "ALTER TABLE child DROP FOREIGN KEY fk_short;" +
            "ALTER TABLE child ADD CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, r.Statements[3].Status);
        Assert.Contains(r.Statements[3].Diagnostics, d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(r.Statements[3].Changes, c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_short FOREIGN KEY(note) REFERENCES parent(id);")]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code);")]
    public void DuplicateExistingFkSymbolIsSemanticInvalidNotUnmodeledSql(string add)
    {
        var r = Analyze(Shared + add);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, r.Statements[2].Status);
        Assert.Empty(r.Statements[2].Changes);
        Assert.Same(r.Statements[2].CatalogBefore, r.Statements[2].CatalogAfter);
    }

    [Theory]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(p) REFERENCES missing(id);")]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(absent) REFERENCES parent(id);")]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(p,q) REFERENCES parent(id);")]
    public void InvalidForeignKeyBindingNeverPublishesKnownCatalogDelta(string add)
    {
        var r = Analyze(Shared + add);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes, c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(p) REFERENCES parent(id) ON DELETE CASCADE;")]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(p) REFERENCES parent(id), DROP FOREIGN KEY fk_short;")]
    [InlineData("ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(p) REFERENCES parent(id) ALGORITHM=INPLACE;")]
    public void CompoundOrActionedAddForeignKeyNeedsSeparatePhysicalAndRowDataProof(string ddl)
    {
        var r = Analyze(Shared + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes, c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void ProviderUnknownIdentityNeverUpgradesAddOrDropFkProof()
    {
        var r = Analyze(Shared + "ALTER TABLE child DROP FOREIGN KEY fk_short;",
            new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[^1].Status);
        Assert.DoesNotContain(r.Statements[^1].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }
}
