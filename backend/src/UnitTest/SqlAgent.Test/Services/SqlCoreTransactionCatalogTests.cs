using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreTransactionCatalogTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Fact]
    public void TransactionalDdlRollback_RestoresCatalogAndRejectsSubsequentSelect()
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE orders(id INTEGER);" +
            "SELECT id FROM orders; ROLLBACK; SELECT id FROM orders;");

        Assert.Equal(5, result.Statements.Count);
        Assert.Equal(SqlTransactionState.Active, result.Statements[0].TransactionAfter);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlTransactionState.Outside, result.Statements[3].TransactionAfter);
        Assert.Equal(SqlEffectKind.ChangeTransaction | SqlEffectKind.ChangeSchema,
                     result.Statements[3].Effects);
        Assert.Single(result.Statements[3].Changes);
        Assert.Empty(result.Statements[3].CatalogAfter.Tables);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[4].CatalogBinding);
        Assert.Contains(result.Statements[4].Diagnostics, d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Empty(result.FinalCatalog.Tables);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void TransactionalDdlCommit_PreservesCreatedTableAndColumnProof()
    {
        var result = Analyze(
            "BEGIN TRANSACTION; CREATE TABLE orders(id INTEGER);" +
            "ALTER TABLE orders ADD COLUMN title TEXT; COMMIT; SELECT title FROM orders;");

        Assert.Equal(SqlTransactionState.Active, result.Statements[2].TransactionAfter);
        Assert.Equal(SqlTransactionState.Outside, result.Statements[3].TransactionAfter);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[4].CatalogBinding);
        Assert.Equal(2, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TransactionalDropRollback_RestoresExistingTableShape()
    {
        var result = Analyze(
            "CREATE TABLE orders(id INTEGER, title TEXT);" +
            "START TRANSACTION; DROP TABLE orders; ROLLBACK; SELECT title FROM orders;");

        Assert.Empty(result.Statements[2].CatalogAfter.Tables);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[4].CatalogBinding);
        Assert.Equal(2, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
    }

    [Fact]
    public void RollbackWithoutCatalogEffects_DoesNotCreatePhantomDeltas()
    {
        var result = Analyze("BEGIN; ROLLBACK;");
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Equal(SqlEffectKind.ChangeTransaction, result.Statements[1].Effects);
    }

    [Theory]
    [InlineData("SAVEPOINT s")]
    [InlineData("ROLLBACK TO SAVEPOINT s")]
    [InlineData("RELEASE SAVEPOINT s")]
    [InlineData("BEGIN ISOLATION LEVEL SERIALIZABLE")]
    public void UnmodeledTransactionCommands_TaintCatalogFailClosed(string command)
    {
        var result = Analyze(
            "CREATE TABLE orders(id INTEGER);" + command + "; SELECT id FROM orders;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[2].CatalogBinding);
    }

    [Fact]
    public void NestedBegin_DoesNotPretendTransactionCanBeRevertedPrecisely()
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE orders(id INTEGER); BEGIN; ROLLBACK; SELECT id FROM orders;");
        Assert.Equal(SqlTransactionState.Unknown, result.Statements[2].TransactionAfter);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[4].CatalogBinding);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void InvalidDdlInsideTransaction_DoesNotAllowRollbackToMintNewProofs()
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE orders(id INTEGER, id TEXT);" +
            "ROLLBACK; SELECT id FROM orders;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Equal(SqlTransactionState.Unknown, result.Statements[1].TransactionAfter);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[3].CatalogBinding);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
    }

    [Fact]
    public void BareRollbackCannotRestoreAnUnprovenPriorCatalog()
    {
        var result = Analyze(
            "CREATE TABLE orders(id INTEGER); ROLLBACK; SELECT id FROM orders;");
        Assert.Equal("SQL_TRANSACTION_WITHOUT_BEGIN", result.Statements[1].Diagnostics[0].Code);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[2].CatalogBinding);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenDialectTransactionsNeverInferPostgresqlRollbackSemantics(SqlAgentToolType dialect)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "BEGIN TRANSACTION; SELECT 1; ROLLBACK;",
            dialect, SqlCatalogState.KnownEmpty());
        Assert.NotEqual(SqlTransactionState.Active, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnsupportedProceduralInsideTransactionInvalidatesRollbackProof()
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE orders(id INTEGER);" +
            "CALL dangerous_procedure(); ROLLBACK; SELECT id FROM orders;");
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[4].CatalogBinding);
    }
}
