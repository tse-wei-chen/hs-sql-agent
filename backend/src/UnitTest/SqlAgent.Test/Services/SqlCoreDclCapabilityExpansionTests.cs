using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreDclCapabilityExpansionTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType provider) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, SqlCatalogState.KnownEmpty());

    [Fact]
    public void MySqlQuotedAccountHostPairsRemainStructuredAndDistinct()
    {
        var analysis = Analyze(
            "GRANT SELECT, INSERT ON app.orders TO 'alice'@'localhost', 'alice'@'127.0.0.1';" +
            "REVOKE SELECT ON app.orders FROM 'alice'@'localhost';",
            SqlAgentToolType.MySQL);
        var grant = analysis.Statements[0];
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, grant.Status);
        Assert.Equal(SqlEffectCoverage.Partial, grant.EffectCoverage);
        Assert.Equal(4, grant.SecurityIntents.Count);
        Assert.Equal(5, analysis.SecurityIntents.Count);
        Assert.All(grant.SecurityIntents, intent =>
        {
            Assert.Equal(SqlSecurityPrincipalKind.MySqlAccount, intent.PrincipalKind);
            Assert.Equal("alice", intent.Principal);
            Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
            Assert.Equal(SqlSecurityAction.Grant, intent.Action);
            Assert.Equal(new[] { "app", "orders" }, intent.Target.Parts);
            Assert.Equal(grant.Syntax.Span.Start, intent.Span.Start);
        });
        Assert.Equal("localhost", grant.SecurityIntents[0].PrincipalHost);
        Assert.Equal("127.0.0.1", grant.SecurityIntents[2].PrincipalHost);
        Assert.Equal(SqlSecurityAction.Revoke, Assert.Single(analysis.Statements[1].SecurityIntents).Action);
        Assert.Equal(SqlStatementEffectOperation.RevokePrivilege,
            Assert.Single(analysis.Statements[1].DetailedEffects).Operation);
        Assert.All(analysis.DetailedEffects, effect =>
        {
            Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
            Assert.Null(effect.ObjectIdentity);
        });
        Assert.Empty(analysis.Diagnostics);
        Assert.True(analysis.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MySqlAccountCaseAndHostAreNeverConflatedWithSqlRoles()
    {
        var result = Analyze(
            "GRANT UPDATE ON prod.orders TO 'Alice'@'HOST';" +
            "GRANT UPDATE ON prod.orders TO 'Alice'@'host';",
            SqlAgentToolType.MySQL);
        Assert.All(result.SecurityIntents, i => Assert.Equal("Alice", i.Principal));
        Assert.Equal("HOST", result.SecurityIntents[0].PrincipalHost);
        Assert.Equal("host", result.SecurityIntents[1].PrincipalHost);
        Assert.Equal(SqlSecurityPrincipalKind.MySqlAccount, result.SecurityIntents[0].PrincipalKind);
    }

    [Theory]
    [InlineData("GRANT SELECT ON app.orders TO alice")]
    [InlineData("GRANT SELECT ON app.orders TO 'alice'")]
    [InlineData("GRANT SELECT ON orders TO 'alice'@'localhost'")]
    [InlineData("GRANT SELECT ON app.* TO 'alice'@'localhost'")]
    [InlineData("GRANT ALL ON app.orders TO 'alice'@'localhost'")]
    [InlineData("GRANT SELECT ON app.orders TO 'alice'@'%'")]
    [InlineData("GRANT SELECT ON app.orders TO 'alice'@'local_host'")]
    [InlineData("GRANT SELECT ON app.orders TO 'alice'@'localhost' WITH GRANT OPTION")]
    [InlineData("GRANT SELECT ON app.orders TO 'alice'@'localhost' IDENTIFIED BY 'pass'")]
    [InlineData("GRANT SELECT ON app.orders TO 'alice'@'localhost',")]
    [InlineData("GRANT SELECT ON app.orders TO CURRENT_USER()")]
    public void MySqlUnmodeledAccountAndPrivilegeShapesDoNotEmitAnIntent(string sql)
    {
        var statement = Assert.Single(Analyze(sql, SqlAgentToolType.MySQL).Statements);
        Assert.Empty(statement.SecurityIntents);
        Assert.NotEqual(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Empty(statement.Changes);
    }

    [Fact]
    public void MySqlScriptAccountSeparatorCannotBeUsedToSmuggleExecutableParameterSyntax()
    {
        // Script segmentation may lex account @ safely, but the strict SQL
        // compiler parser must still reject @ as an unbound SQL parameter.
        var statement = Assert.Single(Analyze("SELECT @secret;", SqlAgentToolType.MySQL).Statements);
        Assert.Equal(SqlScriptParseStatus.Invalid, statement.Syntax.Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Empty(statement.SecurityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Fact]
    public void SqlServerDenyProducesTypedPotentialEffectsWithoutGrantingExecutionAccess()
    {
        var analysis = Analyze(
            "GRANT SELECT ON OBJECT::dbo.orders TO analyst;" +
            "DENY SELECT, UPDATE ON OBJECT::dbo.orders TO analyst;",
            SqlAgentToolType.MsSqlServer);
        var deny = analysis.Statements[1];
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, deny.Status);
        Assert.Equal(SqlEffectKind.ChangePermissions, deny.Effects);
        Assert.Equal(SqlEffectCoverage.Partial, deny.EffectCoverage);
        Assert.Equal(2, deny.SecurityIntents.Count);
        Assert.All(deny.SecurityIntents, intent =>
        {
            Assert.Equal(SqlSecurityAction.Deny, intent.Action);
            Assert.Null(intent.PrincipalHost);
            Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
            Assert.Equal(SqlSecurityPrincipalKind.Named, intent.PrincipalKind);
            Assert.Equal("dbo.orders", intent.Target.DisplayName);
        });
        Assert.All(deny.DetailedEffects, effect =>
        {
            Assert.Equal(SqlStatementEffectOperation.DenyPrivilege, effect.Operation);
            Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
            Assert.Null(effect.ObjectIdentity);
        });
        Assert.Equal(SqlSecurityAction.Grant, analysis.Statements[0].SecurityIntents[0].Action);
        Assert.True(analysis.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "DENY SELECT ON TABLE users TO analyst")]
    [InlineData(SqlAgentToolType.MySQL, "DENY SELECT ON app.orders TO 'alice'@'localhost'")]
    [InlineData(SqlAgentToolType.Sqlite, "DENY SELECT ON users TO analyst")]
    [InlineData(SqlAgentToolType.Oracle, "DENY SELECT ON users TO analyst")]
    [InlineData(SqlAgentToolType.Firebird, "DENY SELECT ON TABLE users TO ROLE analyst")]
    public void OtherDialectsMustNotBorrowSqlServerDeny(SqlAgentToolType provider, string sql)
    {
        var statement = Assert.Single(Analyze(sql, provider).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.SecurityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Theory]
    [InlineData("DENY SELECT ON dbo.orders TO analyst")]
    [InlineData("DENY SELECT ON OBJECT::orders TO analyst")]
    [InlineData("DENY CONTROL ON OBJECT::dbo.orders TO analyst")]
    [InlineData("DENY SELECT ON OBJECT::dbo.orders TO analyst CASCADE")]
    [InlineData("DENY SELECT ON OBJECT::dbo.orders TO analyst AS dbo")]
    [InlineData("DENY SELECT ON OBJECT::dbo.orders TO PUBLIC")]
    public void SqlServerUnmodeledDenyRemainsFailClosed(string sql)
    {
        var statement = Assert.Single(Analyze(sql, SqlAgentToolType.MsSqlServer).Statements);
        Assert.NotEqual(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Empty(statement.SecurityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Fact]
    public void DclIntentsDoNotChangeCatalogOrProveQueryPermission()
    {
        var analysis = Analyze(
            "GRANT SELECT ON app.orders TO 'alice'@'localhost';" +
            "SELECT id FROM app.orders;", SqlAgentToolType.MySQL);
        Assert.Single(analysis.Statements[0].SecurityIntents);
        Assert.Empty(analysis.Statements[0].Changes);
        // Without an explicit MySQL identifier-case profile, name
        // resolution is unproven; DCL must not promote it to Verified.
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, analysis.Statements[1].CatalogBinding);
        Assert.Empty(analysis.FinalCatalog.Tables);
    }
}
