using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Oracle DDL commits before and after execution; its independent COMMIT and
/// ROLLBACK do not undo proven schema declarations. This is static Catalog
/// knowledge, NOT proof that DML, triggers, or any runtime transaction ran.
/// </summary>
public sealed class SqlCoreOracleTransactionBoundaryTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Oracle,
            SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("ROLLBACK", SqlTransactionControlOperation.Rollback)]
    [InlineData("ROLLBACK WORK", SqlTransactionControlOperation.Rollback)]
    [InlineData("COMMIT", SqlTransactionControlOperation.Commit)]
    [InlineData("COMMIT WORK", SqlTransactionControlOperation.Commit)]
    public void IndependentOracleTclPreservesPreviouslyCommittedDdl(
        string command, SqlTransactionControlOperation operation)
    {
        var report = Analyze(
            $"CREATE TABLE orders(ID NUMBER NOT NULL); {command}; " +
            "SELECT ID FROM orders;");
        Assert.Equal(3, report.Statements.Count);
        Assert.Empty(report.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, report.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, report.Statements[1].Status);
        // Oracle's object/name binding can be verified without claiming every
        // expression type was fully semantically analyzed.
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed,
            report.Statements[2].Status);
        var tcl = report.Statements[1];
        Assert.Equal(SqlTransactionState.Outside, tcl.TransactionBefore);
        Assert.Equal(SqlTransactionState.Outside, tcl.TransactionAfter);
        Assert.Equal(SqlEffectKind.ChangeTransaction, tcl.Effects);
        Assert.Empty(tcl.Changes);
        Assert.Single(tcl.CatalogAfter.Tables);
        Assert.Equal(SqlCatalogKnowledge.Known, tcl.Certainty);
        Assert.Equal(SqlTransactionEffectOrigin.TransactionControl,
            tcl.TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.NotApplicable,
            tcl.TransactionProvenance.RollbackScope);
        Assert.Equal(operation, tcl.TransactionProvenance.ControlOperation);
        Assert.False(tcl.TransactionProvenance.IsServerRollbackProven);
        Assert.Equal(SqlCatalogBindingStatus.Verified,
            report.Statements[2].CatalogBinding);
        Assert.True(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("CREATE TABLE orders(ID NUMBER); ROLLBACK;")]
    [InlineData("CREATE TABLE orders(ID NUMBER); COMMIT;")]
    [InlineData("CREATE TABLE orders(ID NUMBER); ALTER TABLE orders ADD (NOTE VARCHAR2(32)); ROLLBACK;")]
    public void OracleDdlAlwaysCarriesPossibleImplicitCommitProvenance(string sql)
    {
        var report = Analyze(sql);
        var ddls = report.Statements.Where(x =>
            x.Syntax.Kind == SqlScriptStatementKind.Schema).ToArray();
        Assert.NotEmpty(ddls);
        Assert.All(ddls, ddl =>
        {
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
            Assert.True(ddl.Effects.HasFlag(SqlEffectKind.ChangeSchema));
            Assert.True(ddl.Effects.HasFlag(SqlEffectKind.ChangeTransaction));
            Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
                ddl.TransactionProvenance.Origin);
            Assert.Equal(SqlTransactionRollbackScope.Unproven,
                ddl.TransactionProvenance.RollbackScope);
            Assert.False(ddl.TransactionProvenance.IsServerRollbackProven);
            Assert.Equal(SqlCatalogKnowledge.Unknown,
                ddl.TransactionProvenance.RuntimeCertainty);
        });
        Assert.True(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    public void OracleTclWithoutExplicitBeginDoesNotTaintKnownEmptyVirtualCatalog(string command)
    {
        var report = Analyze(command + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(report.Statements).Status);
        Assert.Equal(SqlTransactionState.Outside, report.FinalTransaction);
        Assert.Empty(report.FinalCatalog.Tables);
        Assert.True(report.FinalCatalog.IsClosedWorld);
        Assert.Empty(report.Diagnostics);
    }

    [Theory]
    [InlineData("COMMIT FORCE 'unknown'")]
    [InlineData("ROLLBACK TO SAVEPOINT missing")]
    [InlineData("SAVEPOINT point1")]
    public void OracleUnprovenTransactionGrammarNeverMintsRollbackProof(string command)
    {
        var result = Analyze(
            $"CREATE TABLE orders(ID NUMBER); {command}; SELECT ID FROM orders;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified,
            result.Statements[2].CatalogBinding);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
    }

    [Fact]
    public void OraclePostgresStyleBeginTransactionIsNotSupported()
    {
        // Oracle BEGIN is a PL/SQL block keyword, never an implicit DDL
        // transaction boundary. Parsing may consume its incomplete block,
        // so unlike a regular SQL statement it must not assume 3 statements.
        var report = Analyze("BEGIN TRANSACTION;");
        Assert.NotEmpty(report.Statements);
        Assert.DoesNotContain(report.Statements,
            x => x.Status == SqlStatementAnalysisStatus.Analyzed);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OracleCommitCannotReEstablishLostCatalogKnowledge()
    {
        var report = Analyze(
            "CREATE TABLE orders(ID NUMBER); UNRECOGNIZED WORDS; COMMIT;");
        Assert.False(report.FinalCatalog.IsClosedWorld);
        // Recognition of COMMIT does not manufacture missing schema knowledge.
        // The control itself is recognized even when the catalog is unknown.
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, report.Statements[2].Status);
        Assert.False(report.Statements[2].CatalogAfter.IsClosedWorld);
        Assert.False(report.HasCompleteSymbolCatalog);
    }

    [Fact]
    public void OtherDialectTransactionBehaviorIsNotChanged()
    {
        var pg = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE orders(ID INTEGER); ROLLBACK;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, pg.Statements[1].Status);
        Assert.False(pg.FinalCatalog.IsClosedWorld);

        var fb = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE orders(ID INTEGER); ROLLBACK;",
            SqlAgentToolType.Firebird, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, fb.Statements[1].Status);
        Assert.False(fb.FinalCatalog.IsClosedWorld);
    }
}
