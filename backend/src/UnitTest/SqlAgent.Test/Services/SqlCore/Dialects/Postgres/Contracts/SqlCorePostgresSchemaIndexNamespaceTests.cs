using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL standalone indexes live in their parent table's schema
/// and share that schema's relation namespace with ordinary tables.
/// These proofs are static virtual-Catalog statements, never server
/// execution or authorization evidence.
/// </summary>
public sealed class SqlCorePostgresSchemaIndexNamespaceTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            catalog ?? SqlCatalogState.KnownEmpty());

    [Fact]
    public void DifferentExplicitSchemasCanHaveSameStandaloneIndexName()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE INDEX ix_shared ON audit.events(id);");

        Assert.Equal(4, result.Statements.Count);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
        {
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status);
            Assert.Equal(SqlCatalogKnowledge.Known, s.Certainty);
            Assert.Equal(SqlEffectCoverage.Complete, s.EffectCoverage);
        });
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(result.Statements[3].DetailedEffects).Certainty);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.All(result.FinalCatalog.Tables, t =>
            Assert.Equal("ix_shared", Assert.Single(t.Indexes).Name));
    }

    [Fact]
    public void ConstraintInAnotherExplicitSchemaDoesNotHideIndexNamespace()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_events ON audit.events(id);");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[2].Certainty);
        Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "audit.events").Indexes);
        Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.parent").Constraints);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DuplicateIndexNameInSameSchemaIsStillRejected()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE TABLE app.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE INDEX ix_shared ON app.events(id);");

        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Contains(result.Statements[3].Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(result.Statements[3].Changes);
        Assert.Same(result.Statements[3].CatalogBefore,
            result.Statements[3].CatalogAfter);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "app.events").Indexes);
    }

    [Fact]
    public void ConstraintInSameSchemaRemainsUnprovenImplicitIndexCollision()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE app.events(id INTEGER);" +
            "CREATE INDEX ix_events ON app.events(id);");

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            result.Statements[2].Status);
        Assert.Contains(result.Statements[2].Diagnostics,
            x => x.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.DoesNotContain(result.Statements[2].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void IndexNameEqualToSameSchemaTableMustBeRejected()
    {
        var result = Analyze(
            "CREATE TABLE app.ix_shared(id INTEGER);" +
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);");

        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Contains(result.Statements[2].Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(result.Statements[2].Changes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.FinalCatalog.Tables.SelectMany(t => t.Indexes));
    }

    [Fact]
    public void TableNameEqualToExistingIndexInSameSchemaMustBeRejected()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE TABLE app.ix_shared(id INTEGER);");

        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Contains(result.Statements[2].Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(result.Statements[2].Changes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Single(result.FinalCatalog.Tables);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Indexes);
    }

    [Fact]
    public void SameRelationNameInOtherSchemaDoesNotBlockIndexCreation()
    {
        var result = Analyze(
            "CREATE TABLE app.ix_shared(id INTEGER);" +
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON audit.events(id);");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "audit.events").Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AmbiguousUnqualifiedRelationCannotCertifyIndexNamespace()
    {
        var result = Analyze(
            "CREATE TABLE ix_shared(id INTEGER);" +
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);");

        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
        Assert.Empty(result.FinalCatalog.Tables.SelectMany(t => t.Indexes));
    }

    [Fact]
    public void UnqualifiedIndexesRetainConservativeGlobalNameChecks()
    {
        var result = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE TABLE app.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON users(id);" +
            "CREATE INDEX ix_shared ON app.events(id);");

        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
    }

    [Fact]
    public void AmbiguousUnqualifiedDropIndexCannotPickOneOfTwoSchemas()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE INDEX ix_shared ON audit.events(id);" +
            "DROP INDEX ix_shared;");

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[4].Status);
        Assert.Contains(result.Statements[4].Diagnostics,
            x => x.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.Empty(result.Statements[4].Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ExplicitSearchPathCanSelectOneIndexOwner()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE INDEX ix_shared ON audit.events(id);" +
            "SET search_path TO app;" +
            "DROP INDEX ix_shared;");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(result.Statements[5].Changes).Operation);
        Assert.Empty(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.users").Indexes);
        Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "audit.events").Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ConditionalCreateTableSkipsExistingIndexRelationInSameSchema()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE TABLE IF NOT EXISTS app.ix_shared(id INTEGER);");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[2].Certainty);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Same(result.Statements[2].CatalogBefore,
            result.Statements[2].CatalogAfter);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Single(result.FinalCatalog.Tables);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SoleQualifiedIndexDoesNotProveUnqualifiedDropWithoutSearchPath()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "DROP INDEX ix_shared;");

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[2].Status);
        Assert.Contains(result.Statements[2].Diagnostics,
            x => x.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnknownCatalogStillCannotIssueKnownIndexChange()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);",
            SqlCatalogState.Unknown());

        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            result.Statements[1].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[1].Certainty);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.DoesNotContain(result.FinalCatalog.Tables,
            t => t.Knowledge == SqlCatalogKnowledge.Known);
    }
}
