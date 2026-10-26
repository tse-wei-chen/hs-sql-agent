using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Transaction facts are static catalog premises, not live execution proof.</summary>
public sealed class SqlCoreMultiDialectTransactionTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType dialect) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "BEGIN; COMMIT;")]
    [InlineData(SqlAgentToolType.MySQL, "START TRANSACTION; COMMIT;")]
    [InlineData(SqlAgentToolType.MySQL, "BEGIN; ROLLBACK;")]
    [InlineData(SqlAgentToolType.Sqlite, "BEGIN; COMMIT;")]
    [InlineData(SqlAgentToolType.Sqlite, "BEGIN TRANSACTION; ROLLBACK;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "BEGIN TRAN; COMMIT TRAN;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "BEGIN TRANSACTION; ROLLBACK TRANSACTION;")]
    public void ProviderSimpleTransactionsHaveTypedBoundaryAndNoFakeCatalogDelta(
        SqlAgentToolType dialect, string sql)
    {
        var result = Analyze(sql, dialect);
        Assert.Equal(2, result.Statements.Count);
        Assert.All(result.Statements, s =>
        {
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status);
            Assert.Empty(s.Diagnostics);
            Assert.Equal(SqlStatementEffectOperation.TransactionControl, Assert.Single(s.DetailedEffects).Operation);
            Assert.Equal(SqlEffectCoverage.Unknown, s.EffectCoverage);
        });
        Assert.Equal(SqlTransactionState.Active, result.Statements[0].TransactionAfter);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void SavepointRollbackRestoresExactCatalogWithoutClosingTransaction(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE orders(id INTEGER);" +
            "SAVEPOINT mark; ALTER TABLE orders ADD COLUMN note TEXT;" +
            "ROLLBACK TO mark; SELECT id FROM orders; COMMIT;", dialect);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlTransactionState.Active, result.Statements[4].TransactionAfter);
        Assert.Equal(1, Assert.Single(result.Statements[4].CatalogAfter.Tables).Columns.Count);
        if (dialect == SqlAgentToolType.Sqlite)
        {
            // SQLite names may be proven while dynamic type affinity is not.
            Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[5].CatalogBinding);
            Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[5].NameBinding);
        }
        else
            Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[5].CatalogBinding);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(1, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
        Assert.Single(result.Statements[4].Changes);
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[4].Changes[0].Certainty);
    }

    [Fact]
    public void PostgreSqlNestedSavepointReleaseAndOuterRollbackKeepOriginalSnapshot()
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE users(id INTEGER);" +
            "SAVEPOINT one; ALTER TABLE users ADD COLUMN a TEXT;" +
            "SAVEPOINT two; ALTER TABLE users ADD COLUMN b TEXT;" +
            "RELEASE SAVEPOINT one; ROLLBACK; SELECT id FROM users;",
            SqlAgentToolType.Postgres);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[6].Status);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Empty(result.FinalCatalog.Tables);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[8].CatalogBinding);
    }

    [Fact]
    public void PostgreSqlDuplicateSavepointNamesTargetNewest()
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE users(id INTEGER);" +
            "SAVEPOINT s; ALTER TABLE users ADD COLUMN a TEXT;" +
            "SAVEPOINT s; ALTER TABLE users ADD COLUMN b TEXT;" +
            "ROLLBACK TO SAVEPOINT s; RELEASE s;" +
            "SELECT a FROM users; COMMIT;", SqlAgentToolType.Postgres);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[8].CatalogBinding);
        Assert.Equal(2, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SqliteOuterSavepointCanBeginAndCommitTransaction()
    {
        var result = Analyze("SAVEPOINT root; CREATE TABLE users(id INTEGER);" +
            "SAVEPOINT child_mark; ALTER TABLE users ADD COLUMN x TEXT;" +
            "ROLLBACK TO child_mark; RELEASE child_mark; RELEASE root;", SqlAgentToolType.Sqlite);
        Assert.Equal(SqlTransactionState.Active, result.Statements[0].TransactionAfter);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns);
    }

    [Fact]
    public void SqliteNestedBeginCannotOverwriteExistingSavepointTransaction()
    {
        var result = Analyze("SAVEPOINT root; BEGIN; ROLLBACK;", SqlAgentToolType.Sqlite);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SQLServerSaveAndRollbackSavepointDoesNotCommitOuterTransaction()
    {
        var result = Analyze("BEGIN TRAN; SAVE TRAN marker;" +
            "ROLLBACK TRAN marker; COMMIT TRAN;", SqlAgentToolType.MsSqlServer);
        Assert.Equal(4, result.Statements.Count);
        Assert.All(result.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(SqlTransactionState.Active, result.Statements[2].TransactionAfter);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SqlServerNestedBeginWithoutTrancountProofFailsClosed()
    {
        var result = Analyze("BEGIN TRAN; BEGIN TRAN; COMMIT TRAN; ROLLBACK TRAN;",
            SqlAgentToolType.MsSqlServer);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SAVEPOINT marker")]
    [InlineData(SqlAgentToolType.MySQL, "SAVEPOINT marker")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SAVE TRAN marker")]
    public void SavepointRequiresActiveTransaction(SqlAgentToolType dialect, string command)
    {
        var result = Analyze(command, dialect);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "BEGIN; ROLLBACK TO missing;")]
    [InlineData(SqlAgentToolType.Sqlite, "BEGIN; RELEASE missing;")]
    [InlineData(SqlAgentToolType.MySQL, "START TRANSACTION; ROLLBACK TO SAVEPOINT missing;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "BEGIN TRAN; ROLLBACK TRAN missing;")]
    public void MissingSavepointNeverMintsRestoredSnapshot(SqlAgentToolType dialect, string sql)
    {
        var result = Analyze(sql, dialect);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SAVEPOINT \"quoted\"")]
    [InlineData(SqlAgentToolType.Postgres, "BEGIN READ ONLY")]
    [InlineData(SqlAgentToolType.Sqlite, "BEGIN IMMEDIATE")]
    [InlineData(SqlAgentToolType.Sqlite, "SAVEPOINT \"quoted\"")]
    [InlineData(SqlAgentToolType.MySQL, "START TRANSACTION READ ONLY")]
    [InlineData(SqlAgentToolType.MySQL, "COMMIT AND CHAIN")]
    [InlineData(SqlAgentToolType.MsSqlServer, "BEGIN DISTRIBUTED TRANSACTION")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SAVE TRAN marker WITH MARK")]
    public void UnsupportedTransactionVariantsRemainFailClosed(SqlAgentToolType dialect, string sql)
    {
        var result = Analyze(sql, dialect);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MySQLDDLInsideTransactionTriggersPotentialImplicitCommitFence()
    {
        var result = Analyze(
            "START TRANSACTION; CREATE TABLE users(id INTEGER);" +
            "ROLLBACK; SELECT id FROM users;", SqlAgentToolType.MySQL);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN");
        Assert.Equal(SqlTransactionState.Unknown, result.Statements[1].TransactionAfter);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[3].CatalogBinding);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Statements[1].Changes);
    }

    [Fact]
    public void MySQLDMLUnknownStorageEngineCannotPromiseTransactionRollback()
    {
        var result = Analyze("START TRANSACTION; INSERT INTO users(id) VALUES (1); ROLLBACK;",
            SqlAgentToolType.MySQL);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Contains(result.Diagnostics, d => d.Code == "SQL_TRANSACTION_CONTEXT_UNPROVEN");
    }

    [Fact]
    public void PostgreSqlSavepointRollbackRestoresExactSessionScope()
    {
        var result = Analyze(
            "SET search_path TO app; BEGIN; SAVEPOINT mark;" +
            "SET search_path TO sales; ROLLBACK TO mark;",
            SqlAgentToolType.Postgres);
        Assert.Equal(SqlTransactionState.Active, result.FinalTransaction);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.True(result.FinalSession.IsSearchPathProven);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NonTransactionalSQLServerSessionSettingsInsideSavepointCannotRestoreOptions()
    {
        var result = Analyze("BEGIN TRAN; SAVE TRAN mark; SET NOCOUNT ON;" +
            "ROLLBACK TRAN mark;", SqlAgentToolType.MsSqlServer);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.Null(result.FinalSession.NoCount);
    }
}
