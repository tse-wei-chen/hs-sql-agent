using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// All session and catalog snapshots are static premises, never evidence
/// that a transaction or SET command has actually executed on a server.
/// </summary>
public sealed class SqlCoreTransactionSessionSnapshotTests
{
    private static SqlScriptAnalysisResult Pg(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty());

    [Fact]
    public void RollbackToRestoresSearchPathWithoutRollingBackEntireTransaction()
    {
        var result = Pg(
            "CREATE TABLE app.users(id INTEGER, app_id INTEGER);" +
            "CREATE TABLE sales.users(id INTEGER, sales_id INTEGER);" +
            "SET search_path TO app; BEGIN; SAVEPOINT mark;" +
            "SET search_path TO sales; SELECT sales_id FROM users;" +
            "ROLLBACK TO mark; SELECT app_id FROM users; COMMIT;");
        Assert.Equal("sales", result.Statements[6].SessionBefore.SearchPath);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[6].CatalogBinding);
        Assert.Equal(SqlTransactionState.Active, result.Statements[7].TransactionAfter);
        Assert.Equal("app", result.Statements[7].SessionAfter.SearchPath);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[8].CatalogBinding);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RollbackToRestoresBothSchemaIdentityAndSearchPathInSameTransition()
    {
        var result = Pg("SET search_path TO app; BEGIN; CREATE TABLE users(id INTEGER);" +
            "SAVEPOINT s; SET search_path TO sales; CREATE TABLE users(id INTEGER, temp TEXT);" +
            "ROLLBACK TO SAVEPOINT s; SELECT id FROM users; COMMIT;");
        Assert.Equal("sales", result.Statements[6].SessionBefore.SearchPath);
        Assert.Equal("app", result.Statements[6].SessionAfter.SearchPath);
        Assert.Single(result.Statements[6].Changes);
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[6].Changes[0].Certainty);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[7].CatalogBinding);
        Assert.Equal("app.users", Assert.Single(result.FinalCatalog.Tables).Name);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DuplicateSavepointsRestoreMostRecentThenOlderScope()
    {
        var result = Pg(
            "SET search_path TO app; BEGIN; SAVEPOINT s;" +
            "SET search_path TO sales; SAVEPOINT s;" +
            "SET search_path TO archive; ROLLBACK TO s;" +
            "RELEASE SAVEPOINT s; ROLLBACK TO s; COMMIT;");
        Assert.Equal("sales", result.Statements[6].SessionAfter.SearchPath);
        Assert.Equal("sales", result.Statements[7].SessionAfter.SearchPath);
        Assert.Equal("app", result.Statements[8].SessionAfter.SearchPath);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RepeatedRollbackToSavepointRestoresOriginalScopeEachTime()
    {
        var result = Pg(
            "BEGIN; SET search_path TO app; SAVEPOINT mark;" +
            "SET search_path TO sales; ROLLBACK TO mark;" +
            "SET search_path TO archive; ROLLBACK TO mark; COMMIT;");
        Assert.Equal("app", result.Statements[4].SessionAfter.SearchPath);
        Assert.Equal("app", result.Statements[6].SessionAfter.SearchPath);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ReleasePreservesCurrentSearchPathAndCommitKeepsIt()
    {
        var result = Pg("BEGIN; SET search_path TO app; SAVEPOINT mark;" +
            "SET search_path TO sales; RELEASE mark; COMMIT;");
        Assert.Equal("sales", result.Statements[4].SessionAfter.SearchPath);
        Assert.Equal("sales", result.FinalSession.SearchPath);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void FullRollbackAfterSavepointsRestoresPreTransactionSession()
    {
        var result = Pg(
            "SET search_path TO app; BEGIN; SAVEPOINT mark;" +
            "SET search_path TO sales; SAVEPOINT nested;" +
            "SET search_path TO archive; ROLLBACK;");
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void UnknownSessionMutationStillInvalidatesSavepointAndCatalogProofs()
    {
        var result = Pg("SET search_path TO app; BEGIN; SAVEPOINT mark;" +
            "SET ROLE admin; ROLLBACK TO mark;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[3].Status);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalSession.IsSearchPathProven);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Contains(result.Diagnostics, d => d.Code == "SQL_TRANSACTION_CONTEXT_UNPROVEN");
    }

    [Fact]
    public void MySqlUseCannotBeMistakenForTransactionalSearchPathRestoration()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "START TRANSACTION; USE sales; ROLLBACK;",
            SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Null(result.FinalSession.CurrentDatabase);
        Assert.False(result.FinalSession.IsSearchPathProven);
    }

    [Fact]
    public void SQLServerNoCountRemainsConservativeAcrossSavepointRollback()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "BEGIN TRAN; SAVE TRAN mark; SET NOCOUNT ON; ROLLBACK TRAN mark;",
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.Null(result.FinalSession.NoCount);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SavepointsWithoutSessionChangesRetainExistingProviderCatalogSemantics()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "BEGIN; CREATE TABLE users(id INTEGER); SAVEPOINT mark;" +
            "ALTER TABLE users ADD COLUMN note TEXT; ROLLBACK TO mark; COMMIT;",
            SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns);
        Assert.Null(result.FinalSession.SearchPath);
        Assert.Empty(result.Diagnostics);
    }
}
