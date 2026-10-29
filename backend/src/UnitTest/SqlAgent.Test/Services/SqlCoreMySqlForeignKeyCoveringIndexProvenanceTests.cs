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
/// An InnoDB FK's implicit support index MAY disappear on a new covering
/// index; the exact physical result is not a statically proven effect.
/// </summary>
public sealed class SqlCoreMySqlForeignKeyCoveringIndexProvenanceTests
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
        SqlCatalogState? catalog = null,
        SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            catalog ?? SqlCatalogState.KnownEmpty(), profile ?? Linux,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("CREATE INDEX ix_cover ON child(pid)")]
    [InlineData("CREATE INDEX ix_cover ON child(pid,note)")]
    [InlineData("ALTER TABLE child ADD KEY ix_cover(pid,note)")]
    [InlineData("ALTER TABLE child ADD INDEX ix_cover(pid,note)")]
    public void CoveringFkPrefixCreatesTypedIndexWithoutInventingImplicitRemoval(string statement)
    {
        var result = Analyze(Graph + statement + ";");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements,
            step => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var before = result.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "child");
        var after = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Empty(before.Indexes);
        var idx = Assert.Single(after.Indexes);
        Assert.Equal("ix_cover", idx.Name);
        Assert.Equal("pid", idx.Columns[0]);
        Assert.Equal(SqlCatalogConstraintKind.ForeignKey,
            after.Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.ForeignKey).Kind);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        var delta = Assert.Single(result.Statements[2].Changes);
        Assert.Equal(SqlCatalogOperation.CreateIndex, delta.Operation);
        Assert.Equal(SqlCatalogKnowledge.Known, delta.Certainty);
        Assert.Equal("child", delta.TableIdentity!.DisplayName);
    }

    [Theory]
    [InlineData("CREATE INDEX ix_cover ON child(pid)")]
    [InlineData("ALTER TABLE child ADD KEY ix_cover(pid,note)")]
    public void IndexAddMayReplaceImplicitFkIndexSoLaterNameAbsenceIsNotProven(string statement)
    {
        var result = Analyze(Graph + statement + ";" +
            "CREATE INDEX ix_after ON child(note);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Single(result.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes);
    }

    [Theory]
    [InlineData("ALTER TABLE child DROP INDEX ix_cover;")]
    [InlineData("DROP INDEX ix_cover ON child;")]
    public void CannotDropLastFkSupportingExplicitIndexOnUncertainImplicitLineage(string remove)
    {
        var result = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" + remove);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Single(result.Statements[2].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes);
    }

    [Fact]
    public void DroppingFkAfterCoveredIndexDoesNotClearPhysicalIndexUncertainty()
    {
        var result = Analyze(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);" +
            "ALTER TABLE child DROP FOREIGN KEY fk_child;" +
            "CREATE INDEX ix_extra ON child(note);");
        Assert.Empty(result.Statements[2].Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Single(result.Statements[3].CatalogAfter.Tables.Single(t => t.Name == "child").Indexes);
    }

    [Theory]
    [InlineData("CREATE INDEX fk_child ON child(pid,note);")]
    [InlineData("CREATE INDEX FK_CHILD ON child(pid,note);")]
    public void CoveringIndexCannotStealExistingImplicitIndexName(string statement)
    {
        var result = Analyze(Graph + statement);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void MultipleFksStillHaveNoProvenSingleSupportingIndexLineage()
    {
        const string sql =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p1 INT, p2 INT, note INT, " +
            "CONSTRAINT fk_one FOREIGN KEY(p1) REFERENCES parent(id)," +
            "CONSTRAINT fk_two FOREIGN KEY(p2) REFERENCES parent(id));" +
            "CREATE INDEX ix_cover ON child(p1,note);";
        var result = Analyze(sql);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[^1].Status);
        Assert.DoesNotContain(result.Statements[^1].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnknownMySqlIdentifierProfileCannotMintCoveringIndexEvidence()
    {
        var result = Analyze(Graph + "CREATE INDEX ix_cover ON child(pid,note);",
            profile: new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void PostgreSqlDoesNotInheritMySqlImplicitIndexTransition()
    {
        var result = SqlCoreFacade.AnalyzeScript(Graph +
            "CREATE INDEX ix_cover ON child(pid,note);",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }
}
