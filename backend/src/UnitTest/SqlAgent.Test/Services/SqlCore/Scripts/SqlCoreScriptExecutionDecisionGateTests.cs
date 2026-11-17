using System;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// U6 decision gate: the existing Host-owned DML atomic batch is executable
/// only through independent runtime approval. Analyzed caller-supplied TCL
/// never becomes that batch or an executable Script Plan.
/// </summary>
public sealed class SqlCoreScriptExecutionDecisionGateTests
{
    private static SqlCatalogState KnownUsers() =>
        SqlCoreFacade.AnalyzeScript("CREATE TABLE users(id INTEGER);",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken).FinalCatalog;

    [Fact]
    public void HostOwnedTwoStatementDmlBatch_StillAcceptsOnlyMutationStatements()
    {
        var batch = CoreDmlBatchTextParser.ParseDmlBatch(
            "DELETE FROM users WHERE id = 1; DELETE FROM users WHERE id = 2",
            SqlAgentToolType.Postgres);

        Assert.Equal(2, batch.Count);
        Assert.All(batch.Statements, item =>
            Assert.IsType<DeleteStatement>(item.Statement));
    }

    [Fact]
    public void ProvenTclEnvelope_IsAnalyzableButNotAnExecutableDmlBatch()
    {
        const string sql = "BEGIN; UPDATE users SET id = 2 WHERE id = 1; COMMIT;";
        var report = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            KnownUsers(), TestContext.Current.CancellationToken);

        Assert.Equal(SqlScriptCompositionStatus.RejectedForComposition,
            report.CompositionStatus);
        Assert.Equal(SqlDmlEffectDisposition.StaticCommitBoundary,
            Assert.Single(report.DmlEffectReconciliations).Disposition);
        Assert.False(report.IsCompositionExecutionProof);

        Assert.Throws<SqlParseException>(() =>
            CoreDmlBatchTextParser.ParseDmlBatch(sql, SqlAgentToolType.Postgres));
    }

    [Fact]
    public void StaticSavepointRollback_NeverProvesPhysicalRollbackOrDmlAdmission()
    {
        const string sql =
            "BEGIN; UPDATE users SET id = 2 WHERE id = 1;" +
            "SAVEPOINT s; DELETE FROM users WHERE id = 2;" +
            "ROLLBACK TO SAVEPOINT s; COMMIT;";
        var report = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            KnownUsers(), TestContext.Current.CancellationToken);

        Assert.Equal(SqlScriptCompositionStatus.RejectedForComposition,
            report.CompositionStatus);
        var rolledBack = Assert.Single(report.DmlEffectReconciliations,
            item => item.Disposition == SqlDmlEffectDisposition.StaticRollbackExpected);
        Assert.False(rolledBack.IsServerRollbackProven);
        Assert.False(rolledBack.IsExternalSideEffectRollbackProven);
        Assert.False(report.IsCompositionExecutionProof);

        Assert.Throws<SqlParseException>(() =>
            CoreDmlBatchTextParser.ParseDmlBatch(sql, SqlAgentToolType.Postgres));
    }
}
