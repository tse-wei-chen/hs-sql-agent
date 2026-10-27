using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL IF EXISTS must not infer absence without a proven schema
/// selection, and explicit RESTRICT is identical to the default dependency
/// policy, not permission to ignore constraint-backed implicit indexes.
/// </summary>
public sealed class SqlCorePostgresDropIndexRestrictConditionalTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            state ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("DROP INDEX IF EXISTS ix_missing;")]
    [InlineData("DROP INDEX IF EXISTS ix_missing RESTRICT;")]
    public void UnqualifiedConditionalDropWithoutProvenSearchPathDoesNotAssertAbsence(string statement)
    {
        var result = Analyze(statement);
        var drop = Assert.Single(result.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, drop.Certainty);
        Assert.Contains(drop.Diagnostics, d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(drop.Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP INDEX IF EXISTS ix_missing;")]
    [InlineData("DROP INDEX IF EXISTS ix_missing RESTRICT;")]
    public void ProvenSingleSearchPathAndClosedCatalogAllowCertainNoOp(string statement)
    {
        var result = Analyze("SET search_path TO app;" + statement);
        Assert.Equal(2, result.Statements.Count);
        Assert.Empty(result.Diagnostics);
        var drop = result.Statements[1];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, drop.Certainty);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData("DROP INDEX IF EXISTS ix_shared;")]
    [InlineData("DROP INDEX IF EXISTS ix_shared RESTRICT;")]
    public void UnqualifiedConditionalDropUsesOnlyProvenSearchPathSchema(string statement)
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE INDEX ix_shared ON audit.events(id);" +
            "SET search_path TO audit;" +
            statement);

        Assert.Empty(result.Diagnostics);
        var drop = result.Statements[5];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, drop.Certainty);
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(drop.Changes).Operation);
        Assert.Equal("audit.events", Assert.Single(drop.Changes).TableName);
        Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "audit.events").Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ConditionalUnqualifiedDropCanRepeatAfterProvenRemoval()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE INDEX ix_users ON app.users(id);" +
            "SET search_path TO app;" +
            "DROP INDEX IF EXISTS ix_users RESTRICT;" +
            "DROP INDEX IF EXISTS ix_users;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.Statements[4].Changes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
    }

    [Fact]
    public void ExplicitRestrictQualifiedDropHasSameEffectAsDefault()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_shared ON app.users(id);" +
            "CREATE INDEX ix_shared ON audit.events(id);" +
            "DROP INDEX app.ix_shared RESTRICT;");
        Assert.Empty(result.Diagnostics);
        var drop = result.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(drop.Changes).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known, drop.Certainty);
        Assert.Equal(SqlCatalogKnowledge.Known, Assert.Single(drop.DetailedEffects).Certainty);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "app.users").Indexes);
        Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "audit.events").Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void LegacyUnqualifiedStandaloneIndexStillSupportsRestrictWithoutSearchPath()
    {
        var result = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE INDEX ix_users ON users(id);" +
            "DROP INDEX ix_users RESTRICT;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
    }

    [Fact]
    public void ProvenSchemaIndexAbsentButSameNameTableMustNotBeNoOp()
    {
        var result = Analyze(
            "CREATE TABLE app.ix_users(id INTEGER);" +
            "SET search_path TO app;" +
            "DROP INDEX IF EXISTS ix_users;");
        var drop = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Contains(drop.Diagnostics, d => d.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(drop.Changes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnrelatedSchemaIndexDoesNotDisproveScopedConditionalAbsence()
    {
        var result = Analyze(
            "CREATE TABLE audit.events(id INTEGER);" +
            "CREATE INDEX ix_events ON audit.events(id);" +
            "SET search_path TO app;" +
            "DROP INDEX IF EXISTS ix_events RESTRICT;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ImplicitConstraintIndexNameBlocksScopedConditionalNoOp()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER NOT NULL PRIMARY KEY);" +
            "SET search_path TO app;" +
            "DROP INDEX IF EXISTS ix_unknown RESTRICT;");
        var drop = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Contains(drop.Diagnostics, d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.DoesNotContain(drop.Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnqualifiedCatalogOwnerPreventsProvenSearchPathProjection()
    {
        var result = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "SET search_path TO app;" +
            "DROP INDEX IF EXISTS ix_unknown;");
        var drop = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, drop.Certainty);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OpenWorldNeverProvesConditionalAbsenceEvenWithSearchPath()
    {
        var result = Analyze(
            "SET search_path TO app; DROP INDEX IF EXISTS ix_unknown;",
            SqlCatalogState.Unknown());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[1].Certainty);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP INDEX ix RESTRICT;")]
    [InlineData("DROP INDEX IF EXISTS ix RESTRICT;")]
    [InlineData("DROP INDEX app.ix RESTRICT;")]
    public void PostgresOnlyRestrictSyntaxIsNotBorrowedByOtherDialects(string sql)
    {
        foreach (var provider in new[]
        {
            SqlAgentToolType.MySQL, SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Oracle, SqlAgentToolType.Firebird
        })
        {
            var result = SqlCoreFacade.AnalyzeScript(
                sql, provider, SqlCatalogState.KnownEmpty());
            var statement = Assert.Single(result.Statements);
            Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
            Assert.Equal(SqlCatalogKnowledge.Unknown, statement.Certainty);
            Assert.False(result.FinalCatalog.IsClosedWorld);
        }
    }

    [Theory]
    [InlineData("DROP INDEX ix CASCADE;")]
    [InlineData("DROP INDEX IF EXISTS ix CASCADE;")]
    [InlineData("DROP INDEX CONCURRENTLY ix RESTRICT;")]
    [InlineData("DROP INDEX ix, iy RESTRICT;")]
    [InlineData("DROP INDEX app.ix CASCADE;")]
    [InlineData("DROP INDEX ix RESTRICT CASCADE;")]
    public void OutOfScopeModifiersRemainFailClosed(string sql)
    {
        var r = Analyze(sql);
        var drop = Assert.Single(r.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, drop.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, drop.Certainty);
        Assert.Empty(drop.Changes.Where(c => c.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
