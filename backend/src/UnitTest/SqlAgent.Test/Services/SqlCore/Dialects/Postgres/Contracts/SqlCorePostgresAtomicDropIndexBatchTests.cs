using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Multi-index DROP must validate the entire dependency/name set before
/// publishing ANY exact Virtual Catalog delta. This is compile-time
/// simulation, never a guarantee of server execution or authorization.
/// </summary>
public sealed class SqlCorePostgresAtomicDropIndexBatchTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null, SqlAgentToolType provider = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, state ?? SqlCatalogState.KnownEmpty());

    private const string TwoSchemas =
        "CREATE TABLE app.users(id INTEGER, age INTEGER);" +
        "CREATE TABLE audit.events(id INTEGER, age INTEGER);" +
        "CREATE INDEX ix_shared ON app.users(id);" +
        "CREATE INDEX ix_shared ON audit.events(id);";

    [Fact]
    public void FullyQualifiedMultiDropCommitsAllExactDeltasOnce()
    {
        var r = Analyze(TwoSchemas +
            "DROP INDEX app.ix_shared, audit.ix_shared RESTRICT;");
        Assert.Empty(r.Diagnostics);
        Assert.Equal(5, r.Statements.Count);
        var batch = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, batch.Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, batch.EffectCoverage);
        Assert.Equal(2, batch.Changes.Count);
        Assert.All(batch.Changes, x =>
        {
            Assert.Equal(SqlCatalogOperation.DropIndex, x.Operation);
            Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty);
        });
        Assert.Equal(new[] { "app.users", "audit.events" },
            batch.Changes.Select(c => c.TableName).ToArray());
        Assert.Equal(2, batch.DetailedEffects.Count);
        Assert.All(batch.DetailedEffects, x => Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty));
        Assert.All(r.FinalCatalog.Tables, table => Assert.Empty(table.Indexes));
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void TwoIndexesFromSameTableAreRemovedTogether()
    {
        var r = Analyze(
            "CREATE TABLE app.users(id INTEGER, age INTEGER);" +
            "CREATE INDEX ix_a ON app.users(id);" +
            "CREATE INDEX ix_b ON app.users(age);" +
            "DROP INDEX app.ix_a, app.ix_b;");
        Assert.Empty(r.Diagnostics);
        var batch = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(2, batch.Changes.Count);
        Assert.Empty(Assert.Single(r.FinalCatalog.Tables).Indexes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void FailureOnSecondIndexDoesNotPublishPartialFirstDelta()
    {
        var r = Analyze(TwoSchemas + "DROP INDEX app.ix_shared, audit.ix_missing;");
        var batch = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, batch.Certainty);
        Assert.Contains(batch.Diagnostics, d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(batch.Changes, x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(batch.CatalogAfter.IsClosedWorld);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalMissingMemberSkipsOnlyThatMemberAndDropsTheOthers()
    {
        var r = Analyze(TwoSchemas +
            "DROP INDEX IF EXISTS app.ix_shared, audit.ix_missing RESTRICT;");
        Assert.Empty(r.Diagnostics);
        var batch = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, batch.Certainty);
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(batch.Changes).Operation);
        Assert.Equal("app.users", Assert.Single(batch.Changes).TableName);
        Assert.Empty(r.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.Single(r.FinalCatalog.Tables.Single(t => t.Name == "audit.events").Indexes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalAllAbsentIsExactNoOp()
    {
        var r = Analyze("DROP INDEX IF EXISTS app.ix_a, audit.ix_b;");
        var batch = Assert.Single(r.Statements);
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, batch.Certainty);
        Assert.Empty(batch.Changes);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DuplicateRequestedIdentityIsRejectedBeforeAnyMutation()
    {
        var r = Analyze(TwoSchemas +
            "DROP INDEX app.ix_shared, app.ix_shared;");
        var batch = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, batch.Status);
        Assert.Contains(batch.Diagnostics, d => d.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(batch.Changes);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.True(r.FinalCatalog.IsClosedWorld);
        Assert.All(r.FinalCatalog.Tables, t => Assert.Single(t.Indexes));
    }

    [Fact]
    public void TableWithSameIndexNameRejectsWholeBatch()
    {
        var r = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_users ON app.users(id);" +
            "CREATE TABLE audit.ix_users(id INTEGER);" +
            "DROP INDEX app.ix_users, audit.ix_users;");
        var batch = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, batch.Status);
        Assert.Empty(batch.Changes);
        Assert.Same(batch.CatalogBefore, batch.CatalogAfter);
        Assert.Single(r.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnmodeledImplicitIndexOnOneMemberBlocksWholeBatch()
    {
        var r = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_users ON app.users(id);" +
            "CREATE TABLE audit.events(id INTEGER NOT NULL PRIMARY KEY);" +
            "DROP INDEX IF EXISTS app.ix_users, audit.ix_maybe;");
        var batch = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Contains(batch.Diagnostics, d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(batch.Changes, x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MixedQualifiedAndUnqualifiedUsesProvenSearchPathNotGuessing()
    {
        var r = Analyze(TwoSchemas +
            "SET search_path TO audit;" +
            "DROP INDEX ix_shared, app.ix_shared;");
        Assert.Empty(r.Diagnostics);
        var batch = r.Statements[5];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, batch.Status);
        Assert.Equal(2, batch.Changes.Count);
        Assert.All(r.FinalCatalog.Tables, t => Assert.Empty(t.Indexes));
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnqualifiedBatchWithoutSearchPathCannotChooseAQualifiedOwner()
    {
        var r = Analyze(TwoSchemas +
            "DROP INDEX ix_shared, app.ix_shared;");
        var batch = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.DoesNotContain(batch.Changes, x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OpenWorldBatchCannotProveConditionalAbsence()
    {
        var r = Analyze("DROP INDEX IF EXISTS app.ix_a, audit.ix_b;",
            SqlCatalogState.Unknown());
        var batch = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, batch.Certainty);
        Assert.DoesNotContain(batch.Changes, x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP INDEX app.ix_a, app.ix_b CASCADE;")]
    [InlineData("DROP INDEX CONCURRENTLY app.ix_a, app.ix_b;")]
    [InlineData("DROP INDEX app.ix_a, app.ix_b ON app.users;")]
    [InlineData("DROP INDEX app.ix_a, app.ix_b RESTRICT CASCADE;")]
    public void UnmodeledBatchModifiersRemainUnsupported(string sql)
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
    public void PostgresMultiIndexDropIsNeverAcceptedForOtherDialects(
        SqlAgentToolType provider)
    {
        var r = Analyze("DROP INDEX app.ix_a, audit.ix_b;",
            provider: provider);
        var batch = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, batch.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, batch.Certainty);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
