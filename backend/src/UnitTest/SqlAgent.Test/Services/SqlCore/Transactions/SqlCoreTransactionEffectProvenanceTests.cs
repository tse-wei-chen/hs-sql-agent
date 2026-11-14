using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Static script effect provenance is deliberately not a statement execution
/// audit, a rollback guarantee, or authorization for a compiled command.
/// </summary>
public sealed class SqlCoreTransactionEffectProvenanceTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Fact]
    public void PostgresDdlAndSessionInsideTransactionHaveDifferentStaticRollbackScopes()
    {
        var result = Analyze("BEGIN; SET search_path TO app;" +
            "CREATE TABLE users(id INTEGER); SAVEPOINT mark;" +
            "SET search_path TO sales; ROLLBACK TO mark; COMMIT;");
        Assert.Equal(7, result.TransactionProvenance.Count);
        Assert.Equal(SqlTransactionControlOperation.Begin,
            result.Statements[0].TransactionProvenance.ControlOperation);
        var session = result.Statements[1].TransactionProvenance;
        Assert.Equal(SqlTransactionEffectOrigin.SessionMutation, session.Origin);
        Assert.Equal(SqlTransactionRollbackScope.StaticCatalogAndSession, session.RollbackScope);
        Assert.False(session.IsServerRollbackProven);
        Assert.Equal(SqlCatalogKnowledge.Unknown, session.RuntimeCertainty);

        var ddl = result.Statements[2].TransactionProvenance;
        Assert.Equal(SqlTransactionEffectOrigin.CatalogMutation, ddl.Origin);
        Assert.Equal(SqlTransactionRollbackScope.StaticCatalog, ddl.RollbackScope);
        Assert.True(ddl.PotentialEffects.HasFlag(SqlEffectKind.ChangeSchema));

        Assert.Equal(SqlTransactionControlOperation.Savepoint,
            result.Statements[3].TransactionProvenance.ControlOperation);
        var rollbackTo = result.Statements[5].TransactionProvenance;
        Assert.Equal(SqlTransactionControlOperation.RollbackToSavepoint, rollbackTo.ControlOperation);
        Assert.Equal(SqlTransactionRollbackScope.StaticCatalogAndSession, rollbackTo.RollbackScope);
        Assert.Equal("app", result.Statements[5].SessionAfter.SearchPath);
        Assert.Equal(SqlTransactionControlOperation.Commit,
            result.Statements[6].TransactionProvenance.ControlOperation);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void FullRollbackHasTypedBoundaryInsteadOfSqlPrefixHeuristic()
    {
        var result = Analyze("BEGIN; SET search_path TO app; ROLLBACK;");
        var rollback = result.Statements[2].TransactionProvenance;
        Assert.Equal(SqlTransactionEffectOrigin.TransactionControl, rollback.Origin);
        Assert.Equal(SqlTransactionControlOperation.Rollback, rollback.ControlOperation);
        Assert.Equal(SqlTransactionRollbackScope.StaticCatalogAndSession, rollback.RollbackScope);
        Assert.Equal(SqlTransactionState.Active, rollback.TransactionBefore);
        Assert.Equal(SqlTransactionState.Outside, rollback.TransactionAfter);
        Assert.Null(result.FinalSession.SearchPath);
        Assert.False(rollback.IsServerRollbackProven);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MySqlDdlDuringTransactionRecordsImplicitCommitRisk()
    {
        var result = Analyze("START TRANSACTION;" +
            "CREATE TABLE users(id INTEGER); ROLLBACK;", SqlAgentToolType.MySQL);
        var ddl = result.Statements[1].TransactionProvenance;
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit, ddl.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven, ddl.RollbackScope);
        Assert.True(ddl.PotentialEffects.HasFlag(SqlEffectKind.ChangeTransaction));
        Assert.True(ddl.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.False(ddl.IsServerRollbackProven);
        Assert.Equal(SqlTransactionState.Unknown, ddl.TransactionAfter);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN");
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "START TRANSACTION; INSERT INTO users(id) VALUES (1);")]
    [InlineData(SqlAgentToolType.MsSqlServer, "BEGIN TRAN; INSERT INTO users(id) VALUES (1);")]
    public void DmlWithUnprovenProviderRuntimeContractCannotClaimRollback(
        SqlAgentToolType dialect, string sql)
    {
        var result = Analyze(sql, dialect);
        var provenance = result.Statements[1].TransactionProvenance;
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects, provenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven, provenance.RollbackScope);
        Assert.True(provenance.PotentialEffects.HasFlag(SqlEffectKind.WriteData));
        Assert.Equal(SqlTransactionState.Unknown, provenance.TransactionAfter);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void PostgreSqlDmlDoesNotAdvertiseRuntimeRollbackGuarantee()
    {
        // The virtual table must be known before INSERT. A missing table
        // would correctly invalidate the transaction itself before this test
        // could isolate the runtime rollback-evidence boundary.
        var result = Analyze("CREATE TABLE users(id INTEGER); BEGIN;" +
            "INSERT INTO users(id) VALUES (1); COMMIT;");
        var provenance = result.Statements[2].TransactionProvenance;
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects, provenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven, provenance.RollbackScope);
        Assert.True(provenance.PotentialEffects.HasFlag(SqlEffectKind.WriteData));
        Assert.False(provenance.IsServerRollbackProven);
        Assert.Equal(SqlTransactionState.Active, result.Statements[2].TransactionAfter);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT 1;")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT 1;")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT 1;")]
    public void OutsideTransactionDoesNotMintRollbackEvidence(
        SqlAgentToolType dialect, string sql)
    {
        var provenance = Assert.Single(Analyze(sql, dialect).TransactionProvenance);
        Assert.Equal(SqlTransactionEffectOrigin.OutsideTransaction, provenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.NotApplicable, provenance.RollbackScope);
        Assert.Equal(SqlTransactionControlOperation.None, provenance.ControlOperation);
        Assert.False(provenance.IsServerRollbackProven);
    }

    [Fact]
    public void SqlServerNoCountOutsideTransactionHasNoRollbackScope()
    {
        var result = Analyze("SET NOCOUNT ON;", SqlAgentToolType.MsSqlServer);
        var p = Assert.Single(result.TransactionProvenance);
        Assert.Equal(SqlTransactionEffectOrigin.OutsideTransaction, p.Origin);
        Assert.Equal(SqlTransactionRollbackScope.NotApplicable, p.RollbackScope);
        Assert.Equal(SqlStatementEffectOperation.SetNoCount,
            Assert.Single(result.DetailedEffects).Operation);
    }

    [Fact]
    public void SQLiteSavepointRollbackHasStaticCatalogOnlyScope()
    {
        var result = Analyze("BEGIN; CREATE TABLE orders(id INTEGER);" +
            "SAVEPOINT mark; ALTER TABLE orders ADD COLUMN note TEXT;" +
            "ROLLBACK TO mark; COMMIT;", SqlAgentToolType.Sqlite);
        var rollback = result.Statements[4].TransactionProvenance;
        Assert.Equal(SqlTransactionControlOperation.RollbackToSavepoint, rollback.ControlOperation);
        Assert.Equal(SqlTransactionRollbackScope.StaticCatalog, rollback.RollbackScope);
        Assert.Equal(SqlTransactionEffectOrigin.TransactionControl, rollback.Origin);
        Assert.False(rollback.IsServerRollbackProven);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnknownCommandAndUnknownTransactionGetExplicitUnprovenProvenance()
    {
        var result = Analyze("BEGIN; SET ROLE administrator; ROLLBACK;");
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
            result.Statements[1].TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven,
            result.Statements[1].TransactionProvenance.RollbackScope);
        Assert.Equal(SqlTransactionEffectOrigin.UnknownTransactionState,
            result.Statements[2].TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionControlOperation.Rollback,
            result.Statements[2].TransactionProvenance.ControlOperation);
        Assert.Equal(SqlTransactionRollbackScope.Unproven,
            result.Statements[2].TransactionProvenance.RollbackScope);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ScriptFlattenedProvenanceMatchesStatementsExactly()
    {
        var report = Analyze("BEGIN; SAVEPOINT x; ROLLBACK TO x; COMMIT;");
        Assert.Equal(report.Statements.Count, report.TransactionProvenance.Count);
        for (var i = 0; i < report.Statements.Count; i++)
        {
            Assert.Same(report.Statements[i].TransactionProvenance, report.TransactionProvenance[i]);
            Assert.Equal(report.Statements[i].Syntax.Span.Start,
                report.TransactionProvenance[i].Span.Start);
            Assert.Equal(SqlCatalogKnowledge.Unknown, report.TransactionProvenance[i].RuntimeCertainty);
        }
        Assert.Equal(SqlTransactionControlOperation.ReleaseSavepoint,
            Assert.Single(Analyze("BEGIN; SAVEPOINT x; RELEASE x;").Statements
                .Skip(2)).TransactionProvenance.ControlOperation);
    }
}
