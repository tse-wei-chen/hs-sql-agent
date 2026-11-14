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
/// MySQL key-backed physical indexes share the owning table's index namespace.
/// Only fully provable PRIMARY and explicitly named UNIQUE key indexes may
/// coexist with newly created standalone indexes; no FK auto-index inference.
/// </summary>
public sealed class SqlCoreMySqlKeyIndexNamespaceTests
{
    private static readonly SqlProviderCapabilityProfile ExactLinux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null, SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            state ?? SqlCatalogState.KnownEmpty(),
            profile ?? ExactLinux, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("CREATE INDEX ix_note ON orders(note)")]
    [InlineData("ALTER TABLE orders ADD INDEX ix_note(note)")]
    [InlineData("ALTER TABLE orders ADD KEY ix_note(note)")]
    public void PhysicalPrimaryIndexAndNewExplicitIndexCoexistWithoutFabricatedIndexes(string statement)
    {
        var result = Analyze(
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, note INT);" +
            statement + ";");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var table = Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey, Assert.Single(table.Constraints).Kind);
        Assert.Equal("ix_note", Assert.Single(table.Indexes).Name);
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(result.Statements[1].Changes).Operation);
        Assert.Empty(Assert.Single(result.Statements[0].CatalogAfter.Tables).Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.True(result.Statements[1].Effects.HasFlag(SqlEffectKind.ChangeSchema));
    }

    [Fact]
    public void NamedUniqueAndPrimaryIndexesPermitUnrelatedStandaloneIndex()
    {
        const string sql =
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, note INT, " +
            "CONSTRAINT uq_code UNIQUE(code));" +
            "CREATE INDEX ix_note ON orders(note);" +
            "ALTER TABLE orders DROP INDEX ix_note;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var prior = Assert.Single(result.Statements[1].CatalogAfter.Tables);
        Assert.Equal(new[] { SqlCatalogConstraintKind.PrimaryKey, SqlCatalogConstraintKind.Unique },
            prior.Constraints.Select(c => c.Kind));
        Assert.Equal(new[] { "PRIMARY", "uq_code" },
            prior.Constraints.Select(c => c.Kind == SqlCatalogConstraintKind.PrimaryKey ? "PRIMARY" : c.Name));
        Assert.Equal("ix_note", Assert.Single(prior.Indexes).Name);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.Equal(2, Assert.Single(result.FinalCatalog.Tables).Constraints.Count);
    }

    [Theory]
    [InlineData("CREATE INDEX `PRIMARY` ON orders(note);")]
    [InlineData("CREATE INDEX `primary` ON orders(note);")]
    [InlineData("CREATE INDEX `uq_code` ON orders(note);")]
    [InlineData("ALTER TABLE orders ADD KEY `UQ_CODE`(note);")]
    public void PhysicalKeyNameCollisionIsInvalidAndNeverMintsKnownIndex(string statement)
    {
        var result = Analyze(
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, code INT, note INT, " +
            "CONSTRAINT uq_code UNIQUE(code));" + statement);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        var attempted = result.Statements[1];
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, attempted.Status);
        Assert.DoesNotContain(attempted.Changes, x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(Assert.Single(result.Statements[0].CatalogAfter.Tables).Indexes);
    }

    [Theory]
    [InlineData("CREATE TABLE orders(id INT, note INT, UNIQUE(id));")]
    public void UnknownGeneratedIndexIdentityNeverBecomesAnExplicitIndexProof(string create)
    {
        // Both setup forms have non-proven physical names or FK auto-index
        // behavior. The statement under test must never create a Known index.
        var result = Analyze(create + "CREATE INDEX ix_note ON orders(note);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[^1].Status);
        Assert.DoesNotContain(result.Statements[^1].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void NamedSelfForeignKeyCoveringIndexIsProvenButItsPhysicalReplacementRemainsUnknown()
    {
        const string sql =
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, note INT, " +
            "CONSTRAINT fk_self FOREIGN KEY(note) REFERENCES orders(id));" +
            "CREATE INDEX ix_note ON orders(note);" +
            "CREATE INDEX ix_other ON orders(id);";
        var result = Analyze(sql);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal("ix_note", Assert.Single(
            result.Statements[1].CatalogAfter.Tables.Single().Indexes).Name);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(2, result.FinalCatalog.Tables.Single().Indexes.Count);
    }

    [Fact]
    public void UnprovenMySqlIdentifierModeMustNotCreateKeyBackedIndexProof()
    {
        var result = Analyze(
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, note INT);" +
            "CREATE INDEX ix_note ON orders(note);",
            profile: new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void ExistingStandaloneIndexNamesAreCaseInsensitiveEvenWithoutConstraints()
    {
        const string sql =
            "CREATE TABLE orders(id INT, note INT);" +
            "CREATE INDEX ix_note ON orders(note);" +
            "CREATE INDEX `IX_NOTE` ON orders(id);";
        var result = Analyze(sql);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.Equal("ix_note",
            Assert.Single(Assert.Single(result.Statements[1].CatalogAfter.Tables).Indexes).Name);
    }

    [Fact]
    public void PrimaryAndUniqueIndexNamesAreLocalToTheirOwningTable()
    {
        const string sql =
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, note INT);" +
            "CREATE TABLE events(id INT NOT NULL PRIMARY KEY, note INT);" +
            "CREATE INDEX ix_note ON orders(note);" +
            "CREATE INDEX ix_note ON events(note);";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.All(result.FinalCatalog.Tables,
            t => Assert.Equal("ix_note", Assert.Single(t.Indexes).Name));
    }

    [Fact]
    public void PostgreSqlKeyIndexCollisionGuardIsNotBypassedByMySqlProof()
    {
        const string sql =
            "CREATE TABLE orders(id INT NOT NULL PRIMARY KEY, note INT);" +
            "CREATE INDEX ix_note ON orders(note);";
        var result = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty(), TestContext.Current.CancellationToken);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }
}
