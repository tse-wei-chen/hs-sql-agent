using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Script reconciliation is static and non-executable: seeing DML in SQL does
/// not prove it was attempted, nor that SQL or external side effects rolled back.
/// </summary>
public sealed class SqlCoreDmlEffectReconciliationTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType dialect) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);

    [Fact]
    public void PostgresSavepointRollback_OnlyPostMarkDmlIsStaticallyRolledBack()
    {
        var result = Analyze("CREATE TABLE users(id INTEGER); BEGIN;" +
            "INSERT INTO users(id) VALUES (1); SAVEPOINT mark;" +
            "UPDATE users SET id = 2 WHERE id = 1; ROLLBACK TO mark; COMMIT;",
            SqlAgentToolType.Postgres);

        Assert.Empty(result.Diagnostics);
        var items = result.DmlEffectReconciliations;
        Assert.Equal(new[] { 2, 4 }, items.Select(item => item.StatementIndex));

        Assert.Equal(SqlDmlEffectDisposition.StaticCommitBoundary, items[0].Disposition);
        Assert.Equal(6, items[0].BoundaryStatementIndex);
        Assert.Equal(SqlDmlEffectDisposition.StaticRollbackExpected, items[1].Disposition);
        Assert.Equal(5, items[1].BoundaryStatementIndex);
        Assert.Equal("mark", result.Statements[5].TransactionProvenance.SavepointName);

        Assert.All(items, item =>
        {
            Assert.True(item.PotentialEffects.HasFlag(SqlEffectKind.WriteData));
            Assert.False(item.IsExecutionAttemptProven);
            Assert.False(item.IsServerRollbackProven);
            Assert.False(item.IsExternalSideEffectRollbackProven);
        });
        var list = Assert.IsAssignableFrom<IList<SqlDmlEffectReconciliation>>(items);
        Assert.Throws<NotSupportedException>(() => { list[0] = list[0]; });
    }

    [Fact]
    public void SqliteDuplicateSavepoints_UseNewestMarkAndOuterReleaseCommits()
    {
        var result = Analyze("CREATE TABLE users(id INTEGER); SAVEPOINT root;" +
            "INSERT INTO users(id) VALUES (1); SAVEPOINT mark;" +
            "UPDATE users SET id = 2 WHERE id = 1; SAVEPOINT mark;" +
            "DELETE FROM users WHERE id = 2; ROLLBACK TO mark; RELEASE mark;" +
            "ROLLBACK TO mark; RELEASE mark; RELEASE root;",
            SqlAgentToolType.Sqlite);

        // SQLite's name proof does not imply static type-affinity proof.
        Assert.All(result.DmlEffectReconciliations,
            item => Assert.True(item.PotentialEffects.HasFlag(SqlEffectKind.WriteData)));
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Equal(new[] { 2, 4, 6 },
            result.DmlEffectReconciliations.Select(x => x.StatementIndex));
        Assert.Equal(new[]
        {
            SqlDmlEffectDisposition.StaticCommitBoundary,
            SqlDmlEffectDisposition.StaticRollbackExpected,
            SqlDmlEffectDisposition.StaticRollbackExpected
        }, result.DmlEffectReconciliations.Select(x => x.Disposition));
        Assert.Equal(new int?[] { 11, 9, 7 },
            result.DmlEffectReconciliations.Select(x => x.BoundaryStatementIndex));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "START TRANSACTION", "INTEGER")]
    [InlineData(SqlAgentToolType.MsSqlServer, "BEGIN TRAN", "INT")]
    public void UnprovenProviderDml_NeverMintsRollbackOrCommitEvidence(
        SqlAgentToolType dialect, string begin, string idType)
    {
        var report = Analyze($"CREATE TABLE users(id {idType}); {begin};" +
            "INSERT INTO users(id) VALUES (1); ROLLBACK;", dialect);
        var item = Assert.Single(report.DmlEffectReconciliations);
        Assert.Equal(SqlDmlEffectDisposition.Unproven, item.Disposition);
        Assert.Null(item.BoundaryStatementIndex);
        Assert.False(item.IsServerRollbackProven);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }

    [Fact]
    public void OpaqueStatementInvalidatesEarlierPendingDml_AndLateRollbackDoesNotRepair()
    {
        var report = Analyze("CREATE TABLE users(id INTEGER); BEGIN;" +
            "INSERT INTO users(id) VALUES (1);" +
            "CALL dangerous_procedure(); ROLLBACK;", SqlAgentToolType.Postgres);

        var item = Assert.Single(report.DmlEffectReconciliations);
        Assert.Equal(SqlDmlEffectDisposition.Unproven, item.Disposition);
        Assert.Null(item.BoundaryStatementIndex);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }

    [Fact]
    public void OutsideTransactionIsUnscopedNotAnExecutedOrCommittedMutation()
    {
        var report = Analyze("CREATE TABLE users(id INTEGER);" +
            "INSERT INTO users(id) VALUES (1);", SqlAgentToolType.Postgres);

        var item = Assert.Single(report.DmlEffectReconciliations);
        Assert.Equal(SqlDmlEffectDisposition.Unscoped, item.Disposition);
        Assert.Null(item.BoundaryStatementIndex);
        Assert.False(item.IsExecutionAttemptProven);
        Assert.False(item.IsExternalSideEffectRollbackProven);
        Assert.Equal(SqlTransactionState.Outside, report.FinalTransaction);

        // A verified table NAME must not make a rejected assignment appear
        // to be a modeled, potentially successful row mutation.
        var rejected = Analyze("CREATE TABLE users(id INTEGER);" +
            "UPDATE users SET id = 'not an integer' WHERE id = 1;",
            SqlAgentToolType.Postgres);
        Assert.Equal(SqlCatalogBindingStatus.Verified,
            rejected.Statements[1].NameBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid,
            rejected.Statements[1].Status);
        Assert.Equal(SqlDmlEffectDisposition.Unproven,
            Assert.Single(rejected.DmlEffectReconciliations).Disposition);
    }
}
