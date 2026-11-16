using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A cross-domain static assessment must never become a script execution
/// grant, even when prior object identities and TCL syntax are verified.
/// </summary>
public sealed class SqlCoreMixedScriptCompositionTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres,
        SqlCatalogState? catalog = null) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect,
            catalog ?? SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);

    [Fact]
    public void BoundQueryAndMutation_AreAnalyzeOnlyWithStructuredDependencies()
    {
        var catalog = Analyze("CREATE TABLE users(id INTEGER)").FinalCatalog;
        var report = Analyze(
            "UPDATE users SET id = 2 WHERE id = 1; SELECT id FROM users;",
            catalog: catalog);

        Assert.Equal(SqlScriptCompositionStatus.MixedAnalyzeOnly, report.CompositionStatus);
        Assert.Contains(report.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.ReadWriteComposition);
        Assert.Equal(1, report.BoundCrossDomainDependencyCount);
        Assert.False(report.IsCompositionExecutionProof);
        Assert.Equal(SqlCatalogBindingStatus.Verified, report.Statements[1].NameBinding);

        // A Schema + Query mix is still a cross-family script even without
        // DML; a generic family classifier must not label it single-domain.
        var schemaQuery = Analyze(
            "CREATE TABLE other(id INTEGER); SELECT id FROM other;");
        Assert.Equal(SqlScriptCompositionStatus.MixedAnalyzeOnly,
            schemaQuery.CompositionStatus);
        Assert.Contains(schemaQuery.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.CrossDomainComposition);
    }

    [Fact]
    public void SchemaMutationAndTcl_AreRejectedForCrossDomainComposition()
    {
        var report = Analyze("CREATE TABLE users(id INTEGER);" +
            "BEGIN; INSERT INTO users(id) VALUES (1); COMMIT;");

        Assert.Equal(SqlScriptCompositionStatus.RejectedForComposition,
            report.CompositionStatus);
        Assert.Contains(report.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.SchemaMutationComposition);
        Assert.Contains(report.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.TransactionEnvelope);
        Assert.Equal(SqlDmlEffectDisposition.StaticCommitBoundary,
            Assert.Single(report.DmlEffectReconciliations).Disposition);
        Assert.False(report.IsCompositionExecutionProof);
    }

    [Theory]
    [InlineData("GRANT SELECT ON users TO reader; SELECT id FROM users;",
        SqlScriptCompositionConcern.PrivilegedOrProcedural)]
    [InlineData("EXPLAIN SELECT id FROM users; SELECT id FROM users;",
        SqlScriptCompositionConcern.PrivilegedOrProcedural)]
    [InlineData("CALL do_work(); SELECT id FROM users;",
        SqlScriptCompositionConcern.PrivilegedOrProcedural)]
    [InlineData("SET search_path TO app; SELECT 1;",
        SqlScriptCompositionConcern.SessionScope)]
    public void AnalyzeOnlyDomains_AlwaysExposeSeparateProofBarrier(
        string sql, SqlScriptCompositionConcern concern)
    {
        var catalog = Analyze("CREATE TABLE users(id INTEGER)").FinalCatalog;
        var report = Analyze(sql, catalog: catalog);

        Assert.Contains(report.CompositionFindings,
            item => item.Kind == concern);
        Assert.NotEqual(SqlScriptCompositionStatus.MixedAnalyzeOnly, report.CompositionStatus);
        Assert.False(report.IsCompositionExecutionProof);
    }

    [Fact]
    public void OpaqueOrUnboundStatement_PreservesUnknownAndDoesNotInventEdges()
    {
        var opaque = Analyze("CREATE TABLE users(id INTEGER);" +
            "UPDATE users SET id = 2 WHERE id = 1;" +
            "ZORP anything; SELECT id FROM users;");
        Assert.Equal(SqlScriptCompositionStatus.Unknown, opaque.CompositionStatus);
        Assert.Contains(opaque.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.UnknownEffects);
        Assert.DoesNotContain(opaque.StaticDependencyEdges,
            item => item.SuccessorStatementIndex == 3);

        var unbound = Analyze(
            "UPDATE users SET id = 2 WHERE id = 1; SELECT id FROM users",
            catalog: SqlCatalogState.Unknown());
        Assert.Equal(SqlScriptCompositionStatus.Unknown, unbound.CompositionStatus);
        Assert.Contains(unbound.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.UnverifiedVirtualBinding);
        Assert.Empty(unbound.StaticDependencyEdges);

        var singleUnbound = Analyze("SELECT id FROM missing",
            catalog: SqlCatalogState.Unknown());
        Assert.Equal(SqlScriptCompositionStatus.Unknown, singleUnbound.CompositionStatus);
    }

    [Fact]
    public void BatchAndImplicitCommit_AreExplicitCompositionBarriers()
    {
        var batches = Analyze("SELECT 1\nGO\nSELECT 2", SqlAgentToolType.MsSqlServer);
        Assert.Equal(SqlScriptCompositionStatus.RejectedForComposition,
            batches.CompositionStatus);
        Assert.Contains(batches.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.BatchBoundary);
        Assert.False(batches.IsCompositionExecutionProof);

        var mysql = Analyze("START TRANSACTION; CREATE TABLE users(id INTEGER);" +
            "INSERT INTO users(id) VALUES (1); ROLLBACK;", SqlAgentToolType.MySQL);
        Assert.Equal(SqlScriptCompositionStatus.Unknown, mysql.CompositionStatus);
        Assert.Contains(mysql.CompositionFindings,
            item => item.Kind == SqlScriptCompositionConcern.PossibleImplicitCommit);
        Assert.False(mysql.IsCompositionExecutionProof);
    }
}
