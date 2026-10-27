using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Qualified DROP INDEX names refer to an index relation in its own PostgreSQL
/// schema, not to a table. The Virtual Catalog never attests server execution.
/// </summary>
public sealed class SqlCorePostgresQualifiedDropIndexTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            initial ?? SqlCatalogState.KnownEmpty());

    private const string TwoSchemas =
        "CREATE TABLE app.users(id INTEGER, note TEXT);" +
        "CREATE TABLE audit.events(id INTEGER, note TEXT);" +
        "CREATE INDEX ix_shared ON app.users(id);" +
        "CREATE INDEX ix_shared ON audit.events(id);";

    [Fact]
    public void FullyQualifiedDropSelectsCorrectOwnerWithoutSearchPath()
    {
        var r = Analyze(TwoSchemas + "DROP INDEX app.ix_shared;");
        Assert.Equal(5, r.Statements.Count);
        Assert.Empty(r.Diagnostics);
        var drop = r.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, drop.Certainty);
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(drop.Changes).Operation);
        Assert.Equal("app.users", Assert.Single(drop.Changes).TableName);
        Assert.Equal(SqlCatalogKnowledge.Known, Assert.Single(drop.Changes).Certainty);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(drop.DetailedEffects).Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, drop.EffectCoverage);
        Assert.Empty(r.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.Single(r.FinalCatalog.Tables.Single(t => t.Name == "audit.events").Indexes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void QualifiedDropIgnoresDifferentCurrentSearchPath()
    {
        var r = Analyze(TwoSchemas +
            "SET search_path TO audit;" +
            "DROP INDEX app.ix_shared;");
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[5].Status);
        Assert.Empty(r.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.Single(r.FinalCatalog.Tables.Single(t => t.Name == "audit.events").Indexes);
    }

    [Fact]
    public void QualifiedQuotedIdentifierPartsRemainDistinctFromDottedQuotedNames()
    {
        var r = Analyze(
            "CREATE TABLE \"app\".\"users\"(id INTEGER, note TEXT);" +
            "CREATE INDEX \"ix.dot\" ON \"app\".\"users\"(id);" +
            "DROP INDEX \"app\".\"ix.dot\";" +
            "ALTER TABLE \"app\".\"users\" DROP COLUMN id;");
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.Empty(Assert.Single(r.FinalCatalog.Tables).Indexes);
        Assert.Single(Assert.Single(r.FinalCatalog.Tables).Columns);
    }

    [Fact]
    public void UnqualifiedDropStillRequiresSearchPathForQualifiedOwner()
    {
        var r = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_users ON app.users(id);" +
            "DROP INDEX ix_users;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, r.Statements[2].Status);
        Assert.Empty(r.Statements[2].Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void QualifiedDropWithoutAnExistingIndexRemainsUnproven()
    {
        var r = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_users ON app.users(id);" +
            "DROP INDEX audit.ix_users;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, r.Statements[2].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, r.Statements[2].Certainty);
        Assert.Contains(r.Statements[2].Diagnostics,
            x => x.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.Empty(r.Statements[2].Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalQualifiedDropOfKnownAbsentIndexIsExactNoOp()
    {
        var r = Analyze("DROP INDEX IF EXISTS app.ix_absent;");
        var drop = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, drop.Certainty);
        Assert.Empty(drop.Changes);
        Assert.True(drop.CatalogAfter.IsClosedWorld);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Empty(r.Diagnostics);
        Assert.Empty(r.FinalCatalog.Tables);
    }

    [Fact]
    public void ConditionalQualifiedDropRemovesExistingIndex()
    {
        var r = Analyze(TwoSchemas + "DROP INDEX IF EXISTS audit.ix_shared;");
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[4].Status);
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(r.Statements[4].Changes).Operation);
        Assert.Single(r.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.Empty(r.FinalCatalog.Tables.Single(t => t.Name == "audit.events").Indexes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalMissingIndexWithUnknownImplicitConstraintNameIsUnproven()
    {
        var r = Analyze(
            "CREATE TABLE app.users(id INTEGER NOT NULL PRIMARY KEY);" +
            "DROP INDEX IF EXISTS app.ix_unknown;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, r.Statements[1].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, r.Statements[1].Certainty);
        Assert.Contains(r.Statements[1].Diagnostics,
            x => x.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnqualifiedCatalogIndexCannotBeSilentlyAssignedToExplicitSchema()
    {
        var r = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE INDEX ix_users ON users(id);" +
            "CREATE TABLE app.logs(id INTEGER);" +
            "CREATE INDEX ix_users ON app.logs(id);");
        // Name collisions with unqualified owner already invalidate the
        // proof, independent of a later qualified DROP.
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.Empty(r.Statements[3].Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
    }

    [Fact]
    public void QualifiedIndexNameCannotBeReinterpretedAsATableEvenWithIfExists()
    {
        var r = Analyze(
            "CREATE TABLE app.ix_users(id INTEGER);" +
            "DROP INDEX IF EXISTS app.ix_users;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, r.Statements[1].Status);
        Assert.Contains(r.Statements[1].Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(r.Statements[1].Changes);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnqualifiedOwnerWithSameIndexNameCannotBeAssignedToQualifiedSchema()
    {
        var r = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE INDEX ix_users ON users(id);" +
            "DROP INDEX app.ix_users;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, r.Statements[2].Status);
        Assert.Contains(r.Statements[2].Diagnostics,
            x => x.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(r.Statements[2].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OpenWorldCannotProveQualifiedDropOrConditionalAbsence()
    {
        foreach (var sql in new[]
        {
            "DROP INDEX app.ix_missing;",
            "DROP INDEX IF EXISTS app.ix_missing;"
        })
        {
            var result = Analyze(sql, SqlCatalogState.Unknown());
            var drop = Assert.Single(result.Statements);
            Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
            Assert.Equal(SqlCatalogKnowledge.Unknown, drop.Certainty);
            Assert.Empty(drop.Changes.Where(x =>
                x.Certainty == SqlCatalogKnowledge.Known));
            Assert.False(result.FinalCatalog.IsClosedWorld);
        }
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QualifiedPostgresDropIndexSyntaxIsNeverAcceptedAsAnotherDialect(
        SqlAgentToolType provider)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "DROP INDEX app.ix_users;",
            provider, SqlCatalogState.KnownEmpty());
        var statement = Assert.Single(result.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, statement.Certainty);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP INDEX CONCURRENTLY app.ix_users;")]
    [InlineData("DROP INDEX app.ix_users CASCADE;")]
    [InlineData("DROP INDEX app.ix_users, audit.ix_users;")]
    [InlineData("DROP INDEX app.ix_users ON app.users;")]
    [InlineData("DROP INDEX app.ix_users RESTRICT;")]
    [InlineData("DROP INDEX IF EXISTS ix_users;")]
    public void UnmodeledDropModifiersOrUnqualifiedConditionalStayFailClosed(string sql)
    {
        var r = Analyze(sql);
        var drop = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, drop.Certainty);
        Assert.False(r.FinalCatalog.IsClosedWorld);
        Assert.Empty(drop.Changes.Where(x =>
            x.Certainty == SqlCatalogKnowledge.Known));
    }
}
