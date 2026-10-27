using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// An Outside static transaction context is not proof of server autocommit:
/// MySQL supports session autocommit=0 and implicitly started DML work.
/// General, supported non-temporary DDL must always disclose commit risk.
/// </summary>
public sealed class SqlCoreMySqlImplicitCommitEvidenceTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty());

    [Fact]
    public void EverySupportedNonTemporaryMySqlDdlCarriesImplicitCommitEvidence()
    {
        const string sql =
            "CREATE TABLE inventory(id INT, sku VARCHAR(32));" +
            "ALTER TABLE inventory ADD COLUMN qty INT;" +
            "CREATE INDEX idx_inventory_sku ON inventory(sku);" +
            "DROP INDEX idx_inventory_sku ON inventory;" +
            "DROP TABLE inventory;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(5, result.Statements.Count);
        Assert.All(result.Statements, x =>
        {
            Assert.Equal(SqlScriptStatementKind.Schema, x.Syntax.Kind);
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status);
            Assert.True(x.Effects.HasFlag(SqlEffectKind.ChangeSchema));
            Assert.True(x.Effects.HasFlag(SqlEffectKind.ChangeTransaction));
            Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
                x.TransactionProvenance.Origin);
            Assert.Equal(SqlTransactionRollbackScope.Unproven,
                x.TransactionProvenance.RollbackScope);
            Assert.Equal(SqlCatalogKnowledge.Unknown,
                x.TransactionProvenance.RuntimeCertainty);
            Assert.False(x.TransactionProvenance.IsServerRollbackProven);
            Assert.Equal(SqlTransactionState.Outside, x.TransactionBefore);
            Assert.Equal(SqlTransactionState.Outside, x.TransactionAfter);
        });
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void MySqlDdlInsideExplicitTransactionRemainsUnsupportedAndFailClosed()
    {
        var result = Analyze(
            "START TRANSACTION; CREATE TABLE inventory(id INT); ROLLBACK;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        var ddl = result.Statements[1];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, ddl.Status);
        Assert.Equal(SqlTransactionState.Unknown, ddl.TransactionAfter);
        Assert.Empty(ddl.Changes);
        Assert.Contains(ddl.Diagnostics, x =>
            x.Code == "SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN");
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            ddl.TransactionProvenance.Origin);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OnlyMySqlAndOracleReportImplicitCommitForOutsideDdl()
    {
        foreach (var provider in new[] {
            SqlAgentToolType.Postgres, SqlAgentToolType.Sqlite,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.Firebird
        })
        {
            var report = SqlCoreFacade.AnalyzeScript(
                "CREATE TABLE sample(id INTEGER);",
                provider, SqlCatalogState.KnownEmpty());
            Assert.NotEqual(SqlTransactionEffectOrigin.PossibleImplicitCommit,
                report.Statements[0].TransactionProvenance.Origin);
            Assert.False(report.Statements[0].Effects.HasFlag(SqlEffectKind.ChangeTransaction));
        }
        var oracle = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE sample(id NUMBER);",
            SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            oracle.Statements[0].TransactionProvenance.Origin);
    }

    [Fact]
    public void MySqlTemporaryTableSyntaxNeverSilentlyReceivesOrdinaryDdlProof()
    {
        var report = Analyze("CREATE TEMPORARY TABLE scratch(id INT);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(report.Statements).Status);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }
}
