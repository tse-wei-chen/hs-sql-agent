using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreSecurityDependencyTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Fact]
    public void PostgreSqlRoleGrantsProduceDirectedPotentialMembershipEdges()
    {
        var analysis = Analyze("GRANT reporting TO analyst; GRANT analyst TO reader;");
        Assert.Equal(2, analysis.SecurityDependencies.Count);
        Assert.Empty(analysis.SecurityIntents);
        Assert.All(analysis.SecurityDependencies, edge =>
        {
            Assert.Equal(SqlSecurityDependencyKind.RoleMembership, edge.Kind);
            Assert.Equal(SqlSecurityAction.Grant, edge.Action);
            Assert.Equal(SqlCatalogKnowledge.Unknown, edge.Certainty);
            Assert.Null(edge.Table);
            Assert.Null(edge.Privilege);
            Assert.False(edge.WithDelegationOption);
        });
        Assert.Equal("reporting", analysis.SecurityDependencies[0].Role);
        Assert.Equal("analyst", analysis.SecurityDependencies[0].Principal);
        Assert.Equal("analyst", analysis.SecurityDependencies[1].Role);
        Assert.Equal("reader", analysis.SecurityDependencies[1].Principal);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, analysis.Statements[0].Status);
        Assert.Equal(SqlEffectCoverage.Partial, analysis.Statements[0].EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.GrantRoleMembership,
            Assert.Single(analysis.Statements[0].DetailedEffects).Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            Assert.Single(analysis.Statements[0].DetailedEffects).Certainty);
        Assert.True(analysis.FinalCatalog.IsClosedWorld);
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void PgRoleAdminOptionAndRevokeOnlyTheOptionStayDistinct()
    {
        var analysis = Analyze(
            "GRANT editors, reporters TO alice, bob WITH ADMIN OPTION;" +
            "REVOKE ADMIN OPTION FOR editors FROM alice RESTRICT;");
        var grant = analysis.Statements[0];
        Assert.Equal(4, grant.SecurityDependencies.Count);
        Assert.All(grant.SecurityDependencies, e =>
        {
            Assert.Equal(SqlSecurityDependencyKind.RoleMembership, e.Kind);
            Assert.True(e.WithDelegationOption);
            Assert.False(e.RevokeOptionOnly);
            Assert.Equal(SqlSecurityAction.Grant, e.Action);
            Assert.Equal(SqlSecurityCascadeMode.None, e.CascadeMode);
        });
        var revoke = Assert.Single(analysis.Statements[1].SecurityDependencies);
        Assert.Equal(SqlSecurityAction.Revoke, revoke.Action);
        Assert.True(revoke.RevokeOptionOnly);
        Assert.Equal(SqlSecurityCascadeMode.Restrict, revoke.CascadeMode);
        Assert.Equal(SqlStatementEffectOperation.RevokeRoleMembership,
            Assert.Single(analysis.Statements[1].DetailedEffects).Operation);
        Assert.Equal(5, analysis.SecurityDependencies.Count);
    }

    [Fact]
    public void PgGrantOptionAndRevokeGrantOptionReportPossibleTableDelegationDependencies()
    {
        var analysis = Analyze(
            "GRANT SELECT, UPDATE ON TABLE sales.orders TO analyst WITH GRANT OPTION;" +
            "REVOKE GRANT OPTION FOR SELECT ON TABLE sales.orders FROM analyst CASCADE;");
        Assert.Equal(3, analysis.SecurityIntents.Count);
        Assert.Equal(3, analysis.SecurityDependencies.Count);
        var granted = analysis.Statements[0];
        Assert.Equal(2, granted.SecurityIntents.Count);
        Assert.All(granted.SecurityIntents, i => Assert.True(i.WithDelegationOption));
        Assert.All(granted.SecurityDependencies, edge =>
        {
            Assert.Equal(SqlSecurityDependencyKind.TableDelegation, edge.Kind);
            Assert.Equal("sales.orders", edge.Table!.DisplayName);
            Assert.NotNull(edge.Privilege);
            Assert.True(edge.WithDelegationOption);
            Assert.Equal(SqlCatalogKnowledge.Unknown, edge.Certainty);
        });
        var revoked = Assert.Single(analysis.Statements[1].SecurityDependencies);
        Assert.True(revoked.RevokeOptionOnly);
        Assert.Equal(SqlSecurityCascadeMode.Cascade, revoked.CascadeMode);
        Assert.Equal(SqlSecurityAction.Revoke, revoked.Action);
        Assert.Equal(SqlTablePrivilege.Select, revoked.Privilege!.Value);
        Assert.True(Assert.Single(analysis.Statements[1].SecurityIntents).RevokeOptionOnly);
        Assert.Equal(SqlEffectCoverage.Partial, analysis.Statements[1].EffectCoverage);
        Assert.All(analysis.DetailedEffects, e => Assert.Null(e.ObjectIdentity));
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void OrdinaryTableRevokeCarriesPotentialDelegationImpactWithoutInventingAffectedUsers()
    {
        var analysis = Analyze("REVOKE SELECT ON TABLE public.orders FROM analyst RESTRICT;");
        var edge = Assert.Single(analysis.SecurityDependencies);
        Assert.False(edge.RevokeOptionOnly);
        Assert.Equal(SqlSecurityCascadeMode.Restrict, edge.CascadeMode);
        Assert.Equal("analyst", edge.Principal);
        Assert.Equal(SqlCatalogKnowledge.Unknown, edge.Certainty);
        Assert.Single(analysis.SecurityIntents);
        Assert.Empty(analysis.Statements[0].Changes);
    }

    [Fact]
    public void RoleGraphDoesNotBecomeExecutionAuthorizationOrPhysicalCatalogProof()
    {
        var analysis = Analyze("GRANT reporting TO analyst;" +
            "GRANT SELECT ON TABLE private_data TO reporting;" +
            "SELECT secret FROM private_data;");
        Assert.Single(analysis.SecurityDependencies);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, analysis.Statements[2].CatalogBinding);
        Assert.Equal(SqlCatalogKnowledge.Unknown, analysis.SecurityDependencies[0].Certainty);
        Assert.Empty(analysis.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "GRANT reporting TO analyst")]
    [InlineData(SqlAgentToolType.MsSqlServer, "GRANT reporting TO analyst")]
    [InlineData(SqlAgentToolType.Firebird, "GRANT reporting TO analyst")]
    [InlineData(SqlAgentToolType.Oracle, "GRANT reporting TO analyst")]
    [InlineData(SqlAgentToolType.Sqlite, "GRANT reporting TO analyst")]
    public void ProviderSpecificRoleMembershipIsNotBorrowedAcrossSixDialects(
        SqlAgentToolType provider, string sql)
    {
        var statement = Assert.Single(Analyze(sql, provider).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.SecurityDependencies);
        Assert.Empty(statement.SecurityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Theory]
    [InlineData("GRANT reporting TO PUBLIC")]
    [InlineData("GRANT reporting TO analyst WITH INHERIT TRUE")]
    [InlineData("GRANT reporting TO analyst WITH SET FALSE")]
    [InlineData("GRANT reporting TO analyst GRANTED BY owner")]
    [InlineData("GRANT reporting TO analyst WITH ADMIN TRUE")]
    [InlineData("REVOKE ADMIN OPTION FOR reporting FROM analyst CASCADE GRANTED BY owner")]
    [InlineData("GRANT reporting TO analyst,")]
    [InlineData("GRANT reporting TO analyst WITH ADMIN OPTION WITH GRANT OPTION")]
    [InlineData("GRANT SELECT ON TABLE t TO PUBLIC WITH GRANT OPTION")]
    [InlineData("GRANT SELECT ON TABLE t TO analyst WITH ADMIN OPTION")]
    [InlineData("REVOKE GRANT OPTION FOR SELECT ON TABLE t FROM analyst WITH GRANT OPTION")]
    public void UnmodeledMembershipAndDelegationFormsNeverPartiallyEmitEdges(string sql)
    {
        var statement = Assert.Single(Analyze(sql).Statements);
        Assert.Empty(statement.SecurityDependencies);
        Assert.Empty(statement.SecurityIntents);
        Assert.NotEqual(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "GRANT SELECT ON OBJECT::dbo.orders TO analyst WITH GRANT OPTION")]
    [InlineData(SqlAgentToolType.MySQL, "GRANT SELECT ON app.orders TO 'alice'@'localhost' WITH GRANT OPTION")]
    [InlineData(SqlAgentToolType.Firebird, "GRANT SELECT ON TABLE orders TO USER alice WITH GRANT OPTION")]
    public void ProviderDelegationOptionsNeedDedicatedSemanticsBeforeTheyAreAdmitted(SqlAgentToolType dialect, string sql)
    {
        var statement = Assert.Single(Analyze(sql, dialect).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.SecurityDependencies);
        Assert.Empty(statement.SecurityIntents);
    }

    [Fact]
    public void DclRoleMembershipStillInvalidatesUnknownTransactionControlFlow()
    {
        var analysis = Analyze("BEGIN; GRANT reader TO analyst; ROLLBACK; SELECT 1;");
        Assert.Equal(SqlTransactionState.Unknown, analysis.FinalTransaction);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Single(analysis.SecurityDependencies);
        Assert.Equal(SqlCatalogKnowledge.Unknown, analysis.SecurityDependencies[0].Certainty);
    }

    [Fact]
    public void DependencyExpansionHasBoundedCartesianProduct()
    {
        var many = string.Join(", ", Enumerable.Range(0, 140).Select(i => "user" + i));
        var analysis = Analyze("GRANT reporting TO " + many + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(analysis.Statements).Status);
        Assert.Empty(analysis.SecurityDependencies);
    }

    [Fact]
    public void ExistingMySqlAndSqlServerPrivilegeIntentsRemainBackwardCompatible()
    {
        var mysql = Analyze("GRANT SELECT ON app.orders TO 'alice'@'localhost';", SqlAgentToolType.MySQL);
        Assert.Single(mysql.SecurityIntents);
        Assert.Empty(mysql.SecurityDependencies);
        var mssql = Analyze("DENY SELECT ON OBJECT::dbo.orders TO analyst;", SqlAgentToolType.MsSqlServer);
        Assert.Single(mssql.SecurityIntents);
        Assert.Empty(mssql.SecurityDependencies);
        Assert.Equal(SqlSecurityAction.Deny, mssql.SecurityIntents[0].Action);
    }
}
