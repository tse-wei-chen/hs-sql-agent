using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Firebird RECREATE TABLE is a single atomic metadata operation:
/// it drops a proven existing table then creates the new shape, and
/// must not bypass FK dependency proofs or carry old indexes/columns.
/// </summary>
public sealed class SqlCoreFirebirdRecreateTableTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null, SqlAgentToolType dialect = SqlAgentToolType.Firebird) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect,
            state ?? SqlCatalogState.KnownEmpty(), null,
            TestContext.Current.CancellationToken);

    [Fact]
    public void RecreateAbsentTableCreatesExactCatalogWithoutPhantomDrop()
    {
        var report = Analyze("RECREATE TABLE docs (id INTEGER, label VARCHAR(32));");
        Assert.Empty(report.Diagnostics);
        var statement = Assert.Single(report.Statements);
        Assert.Equal(SqlScriptStatementKind.Schema, statement.Syntax.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, statement.Certainty);
        Assert.Equal(SqlCatalogOperation.CreateTable,
            Assert.Single(statement.Changes).Operation);
        Assert.Equal(new[] { "ID", "LABEL" },
            Assert.Single(report.FinalCatalog.Tables).Columns.Select(x => x.Name));
    }

    [Fact]
    public void RecreateExistingTableReplacesAllColumnsIndexesAndConstraintsAtomically()
    {
        const string sql =
            "CREATE TABLE docs (id INTEGER, note VARCHAR(32));" +
            "CREATE INDEX idx_note ON docs(note);" +
            "RECREATE TABLE docs (new_id INTEGER NOT NULL, state VARCHAR(32));";
        var report = Analyze(sql);
        Assert.Empty(report.Diagnostics);
        Assert.All(report.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var recreate = report.Statements[2];
        Assert.Equal(new[] { SqlCatalogOperation.DropTable, SqlCatalogOperation.CreateTable },
            recreate.Changes.Select(x => x.Operation));
        Assert.All(recreate.Changes, x => Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty));
        Assert.Single(Assert.Single(recreate.CatalogBefore.Tables).Indexes);
        var after = Assert.Single(recreate.CatalogAfter.Tables);
        Assert.Equal(new[] { "NEW_ID", "STATE" }, after.Columns.Select(c => c.Name));
        Assert.Empty(after.Indexes);
        Assert.Empty(after.Constraints);
        Assert.True(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void RecreateTwiceRetainsOnlyLatestShape()
    {
        var report = Analyze(
            "RECREATE TABLE docs (id INTEGER);" +
            "RECREATE TABLE docs (code VARCHAR(32));" +
            "RECREATE TABLE docs (status SMALLINT);");
        Assert.Empty(report.Diagnostics);
        Assert.All(report.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        Assert.Equal("STATUS", Assert.Single(
            Assert.Single(report.FinalCatalog.Tables).Columns).Name);
        Assert.Equal(2, report.Statements[1].Changes.Count);
        Assert.Equal(2, report.Statements[2].Changes.Count);
    }

    [Fact]
    public void DropAndRecreateAllowsPreviouslyUsedIndexName()
    {
        var report = Analyze(
            "CREATE TABLE docs (id INTEGER, label VARCHAR(32));" +
            "CREATE INDEX idx_docs ON docs(label);" +
            "RECREATE TABLE docs (id INTEGER);" +
            "ALTER TABLE docs ADD label VARCHAR(32);" +
            "CREATE INDEX idx_docs ON docs(label);");
        Assert.Empty(report.Diagnostics);
        Assert.All(report.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        Assert.Equal("IDX_DOCS",
            Assert.Single(Assert.Single(report.FinalCatalog.Tables).Indexes).Name);
    }

    [Fact]
    public void InboundForeignKeyBlocksRecreateWithoutPublishingPartialDeltas()
    {
        var report = Analyze(
            "CREATE TABLE parent (id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child (id INTEGER, parent_id INTEGER, " +
            "CONSTRAINT fk_child FOREIGN KEY (parent_id) REFERENCES parent(id));" +
            "RECREATE TABLE parent (new_id INTEGER);");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, report.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, report.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, report.Statements[2].Status);
        Assert.Empty(report.Statements[2].Changes);
        Assert.Same(report.Statements[2].CatalogBefore, report.Statements[2].CatalogAfter);
        Assert.Equal("ID", Assert.Single(
            report.FinalCatalog.Tables.Single(t => t.Name == "PARENT").Columns).Name);
    }

    [Theory]
    [InlineData("RECREATE TABLE docs (id INTEGER, id SMALLINT);")]
    [InlineData("RECREATE TABLE docs (id INTEGER, CHECK(id > 0));")]
    [InlineData("RECREATE GLOBAL TEMPORARY TABLE docs (id INTEGER);")]
    [InlineData("RECREATE TABLE docs AS SELECT 1;")]
    [InlineData("RECREATE TABLE docs (id INTEGER) WITH NO DATA;")]
    public void UnsupportedOrInvalidReplacementNeverChangesKnownTable(string ddl)
    {
        var report = Analyze("CREATE TABLE docs (id INTEGER, note VARCHAR(32));" + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, report.Statements[1].Status);
        // Rejected or unsupported DDL cannot publish a Known successful
        // replacement. Unsupported syntax may conservatively taint the
        // resulting Catalog; it must not overwrite the input snapshot.
        Assert.DoesNotContain(report.Statements[1].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Equal(new[] { "ID", "NOTE" },
            Assert.Single(report.Statements[1].CatalogBefore.Tables)
                .Columns.Select(c => c.Name));
    }

    [Fact]
    public void UnknownCatalogCannotCertifyRecreateAsKnown()
    {
        var report = Analyze("RECREATE TABLE docs (id INTEGER);", SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(report.Statements).Status);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void RecreateTableIsFirebirdOnly(SqlAgentToolType provider)
    {
        var report = Analyze("RECREATE TABLE docs (id INTEGER);", dialect: provider);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(report.Statements).Status);
    }
}
