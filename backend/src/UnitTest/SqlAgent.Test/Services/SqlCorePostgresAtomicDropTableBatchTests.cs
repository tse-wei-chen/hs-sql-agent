using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Multi-table PostgreSQL DROP must prove the entire set, including inbound
/// FK dependencies, before any known Virtual Catalog change is published.
/// This is static analysis, not actual DDL execution or authorization.
/// </summary>
public sealed class SqlCorePostgresAtomicDropTableBatchTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null, SqlAgentToolType provider = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, state ?? SqlCatalogState.KnownEmpty());

    private const string ParentChild =
        "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
        "CREATE TABLE app.child(id INTEGER, parent_id INTEGER," +
        "CONSTRAINT fk_parent FOREIGN KEY(parent_id) REFERENCES app.parent(id));";

    [Theory]
    [InlineData("DROP TABLE app.parent, app.child;")]
    [InlineData("DROP TABLE app.child, app.parent RESTRICT;")]
    public void ParentAndReferencingChildInSameBatchCanDropTogether(string dropSql)
    {
        var r = Analyze(ParentChild + dropSql);
        Assert.Equal(3, r.Statements.Count);
        Assert.Empty(r.Diagnostics);
        var batch = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, batch.Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, batch.EffectCoverage);
        Assert.Equal(2, batch.Changes.Count);
        Assert.Equal(2, batch.DetailedEffects.Count);
        Assert.All(batch.Changes, c =>
        {
            Assert.Equal(SqlCatalogOperation.DropTable, c.Operation);
            Assert.Equal(SqlCatalogKnowledge.Known, c.Certainty);
        });
        Assert.All(batch.DetailedEffects, e =>
            Assert.Equal(SqlCatalogKnowledge.Known, e.Certainty));
        Assert.Empty(r.FinalCatalog.Tables);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ExternalForeignKeyBlocksEntireBatchNotJustReferencedParent()
    {
        var r = Analyze(ParentChild +
            "CREATE TABLE audit.outside(id INTEGER, parent_id INTEGER," +
            "CONSTRAINT fk_outside FOREIGN KEY(parent_id) REFERENCES app.parent(id));" +
            "DROP TABLE app.child, app.parent;");
        Assert.Empty(r.Statements[0].Diagnostics);
        Assert.Empty(r.Statements[1].Diagnostics);
        Assert.Empty(r.Statements[2].Diagnostics);
        var batch = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, batch.Status);
        Assert.Contains(batch.Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(batch.Changes);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
        Assert.Equal(3, r.FinalCatalog.Tables.Count);
    }

    [Fact]
    public void InvalidSecondNameDoesNotPublishFirstDrop()
    {
        var r = Analyze(ParentChild + "DROP TABLE app.child, app.missing;");
        var batch = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, batch.Status);
        Assert.Empty(batch.Changes);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.Equal(2, r.FinalCatalog.Tables.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalMissingTargetIsSkippedButOtherTableDrops()
    {
        var r = Analyze(ParentChild +
            "DROP TABLE IF EXISTS app.missing, app.child RESTRICT;");
        var batch = r.Statements[2];
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(SqlCatalogOperation.DropTable, Assert.Single(batch.Changes).Operation);
        Assert.Equal("app.child", Assert.Single(batch.Changes).TableName);
        Assert.Single(r.FinalCatalog.Tables);
        Assert.Equal("app.parent", Assert.Single(r.FinalCatalog.Tables).Name);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AllMissingConditionalTargetsAreExactNoOp()
    {
        var r = Analyze("DROP TABLE IF EXISTS app.missing, audit.missing RESTRICT;");
        var batch = Assert.Single(r.Statements);
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, batch.Certainty);
        Assert.Empty(batch.Changes);
        Assert.Empty(batch.DetailedEffects);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DuplicatePhysicalNameFailsBeforeMutation()
    {
        var r = Analyze(ParentChild + "DROP TABLE app.child, app.child;");
        var batch = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, batch.Status);
        Assert.Contains(batch.Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(batch.Changes);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MixedExplicitAndSessionResolvedNameDetectsDuplicate()
    {
        var r = Analyze(ParentChild +
            "SET search_path TO app;" +
            "DROP TABLE parent, app.parent;");
        var batch = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, batch.Status);
        Assert.Empty(batch.Changes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MixedQualifiedAndUnqualifiedWithProvenScopeDropsBoth()
    {
        var r = Analyze(ParentChild +
            "SET search_path TO app;" +
            "DROP TABLE child, app.parent RESTRICT;");
        Assert.Empty(r.Diagnostics);
        var batch = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(2, batch.Changes.Count);
        Assert.Empty(r.FinalCatalog.Tables);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnqualifiedWithoutProvenSearchPathCannotChooseSchemaOwner()
    {
        var r = Analyze(ParentChild +
            "DROP TABLE child, app.parent;");
        var batch = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, batch.Certainty);
        Assert.DoesNotContain(batch.Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ForeignKeyFromUnqualifiedCatalogOwnerRemainsUnproven()
    {
        var r = Analyze(
            "CREATE TABLE parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(id INTEGER, parent_id INTEGER," +
            "FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "DROP TABLE app.parent, app.child;");
        var batch = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Empty(batch.Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void QuotedDotIsNotStructuralSchemaSeparator()
    {
        var r = Analyze(
            "SET search_path TO app;" +
            "CREATE TABLE \"app.child\"(id INTEGER);" +
            "CREATE TABLE app.child(id INTEGER);" +
            "DROP TABLE app.child, \"app.child\" RESTRICT;");
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.Equal(2, r.Statements[3].Changes.Count);
        Assert.Empty(r.FinalCatalog.Tables);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ExistingIndexRelationIsNotAnAbsentTableUnderIfExists()
    {
        var r = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_users ON app.users(id);" +
            "DROP TABLE IF EXISTS app.users, app.ix_users;");
        var batch = r.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, batch.Status);
        Assert.Empty(batch.Changes);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.Single(r.FinalCatalog.Tables);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OpenWorldMustNotMintKnownBatchOrConditionalAbsence()
    {
        var r = Analyze("DROP TABLE IF EXISTS app.one, app.two;",
            SqlCatalogState.Unknown());
        var batch = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, batch.Certainty);
        Assert.DoesNotContain(batch.Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP TABLE app.parent, app.child CASCADE;")]
    [InlineData("DROP TABLE app.parent, app.child RESTRICT CASCADE;")]
    [InlineData("DROP TABLE app.parent, app.child ON app.parent;")]
    public void UnsupportedModifiersStayFailClosed(string sql)
    {
        var r = Analyze(sql);
        var batch = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void BatchSyntaxDoesNotBorrowPostgresProofForOtherProviders(SqlAgentToolType dialect)
    {
        var r = Analyze("DROP TABLE app.child, app.parent RESTRICT;", provider: dialect);
        var batch = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, batch.Certainty);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
