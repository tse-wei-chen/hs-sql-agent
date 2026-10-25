using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Static DCL syntax/intent, not authorization or live grant state.</summary>
public sealed class SqlCoreDclSecurityAnalyzerTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType provider = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, SqlCatalogState.KnownEmpty());

    [Fact]
    public void PostgresGrantAndRevokeYieldTypedIntentsWithoutClaimingEffectivePermissions()
    {
        var analysis = Analyze(
            "GRANT SELECT, INSERT ON TABLE app.orders TO analyst, writer;" +
            "REVOKE DELETE ON TABLE app.orders FROM analyst;");
        Assert.Equal(2, analysis.Statements.Count);
        var grant = analysis.Statements[0];
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, grant.Status);
        Assert.Equal(SqlEffectKind.ChangePermissions, grant.Effects);
        Assert.Equal(SqlCatalogKnowledge.Unknown, grant.Certainty);
        Assert.Equal(SqlEffectCoverage.Partial, grant.EffectCoverage);
        Assert.Equal(4, grant.SecurityIntents.Count);
        Assert.All(grant.SecurityIntents, intent =>
        {
            Assert.Equal(SqlSecurityAction.Grant, intent.Action);
            Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
            Assert.Equal(SqlSecurityPrincipalKind.Named, intent.PrincipalKind);
            Assert.Equal("app.orders", intent.Target.DisplayName);
            Assert.Equal(new[] { "app", "orders" }, intent.Target.Parts);
            Assert.Equal(grant.Syntax.Span.Start, intent.Span.Start);
        });
        Assert.Equal(new[] { SqlTablePrivilege.Select, SqlTablePrivilege.Insert },
            grant.SecurityIntents.Take(2).Select(x => x.Privilege));
        Assert.Equal(new[] { "analyst", "writer" },
            grant.SecurityIntents.Select(x => x.Principal).Distinct());
        Assert.All(grant.DetailedEffects, e =>
        {
            Assert.Equal(SqlStatementEffectOperation.GrantPrivilege, e.Operation);
            Assert.Equal(SqlCatalogKnowledge.Unknown, e.Certainty);
            Assert.Null(e.ObjectIdentity);
        });
        var revoke = Assert.Single(analysis.Statements[1].SecurityIntents);
        Assert.Equal(SqlSecurityAction.Revoke, revoke.Action);
        Assert.Equal(SqlTablePrivilege.Delete, revoke.Privilege);
        Assert.Equal(SqlStatementEffectOperation.RevokePrivilege,
            Assert.Single(analysis.Statements[1].DetailedEffects).Operation);
        Assert.Equal(5, analysis.SecurityIntents.Count);
        Assert.Empty(analysis.Diagnostics);
        Assert.Empty(analysis.FinalCatalog.Tables);
        Assert.True(analysis.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void QuotedDclIdentifiersPreserveStructuralIdentity()
    {
        var analysis = Analyze(
            "GRANT SELECT ON TABLE \"a.b\" TO \"ReadOnly\";" +
            "GRANT SELECT ON TABLE a.b TO readonly;");
        var first = Assert.Single(analysis.Statements[0].SecurityIntents);
        var second = Assert.Single(analysis.Statements[1].SecurityIntents);
        Assert.Single(first.Target.Parts);
        Assert.Equal(2, second.Target.Parts.Count);
        Assert.NotEqual(first.Target.Key, second.Target.Key);
        Assert.Equal("ReadOnly", first.Principal);
    }

    [Fact]
    public void StaticDclDoesNotGrantReadPermissionToLaterQueries()
    {
        var analysis = Analyze(
            "GRANT SELECT ON TABLE private_data TO analyst;" +
            "SELECT secret FROM private_data;");
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, analysis.Statements[0].Status);
        Assert.Empty(analysis.Statements[0].Changes);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, analysis.Statements[1].CatalogBinding);
        Assert.Contains(analysis.Statements[1].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
    }

    [Theory]
    [InlineData("GRANT SELECT ON TABLE orders TO PUBLIC")]
    [InlineData("REVOKE INSERT ON orders FROM PUBLIC")]
    public void PostgresPublicPrincipalCannotBeConfusedWithNamedRole(string sql)
    {
        var intent = Assert.Single(Assert.Single(Analyze(sql).Statements).SecurityIntents);
        Assert.Equal(SqlSecurityPrincipalKind.Public, intent.PrincipalKind);
        Assert.Equal("PUBLIC", intent.Principal);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "GRANT SELECT ON TABLE users TO analyst")]
    [InlineData(SqlAgentToolType.Oracle, "GRANT SELECT ON users TO analyst")]
    [InlineData(SqlAgentToolType.Firebird, "GRANT SELECT ON TABLE users TO ROLE analyst")]
    [InlineData(SqlAgentToolType.MsSqlServer, "GRANT SELECT ON OBJECT::dbo.users TO analyst")]
    public void DialectSpecificGrantSubsetIsAnalyzedButNeverAuthorized(SqlAgentToolType dialect, string sql)
    {
        var analysis = Analyze(sql, dialect);
        var statement = Assert.Single(analysis.Statements);
        var intent = Assert.Single(statement.SecurityIntents);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlSecurityAction.Grant, intent.Action);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
        Assert.Null(Assert.Single(statement.DetailedEffects).ObjectIdentity);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "REVOKE UPDATE ON TABLE users FROM analyst")]
    [InlineData(SqlAgentToolType.Oracle, "REVOKE UPDATE ON users FROM analyst")]
    [InlineData(SqlAgentToolType.Firebird, "REVOKE UPDATE ON TABLE users FROM USER alex")]
    [InlineData(SqlAgentToolType.MsSqlServer, "REVOKE UPDATE ON OBJECT::dbo.users FROM analyst")]
    public void DialectSpecificRevokeIsOnlyPotentialPermissionChange(SqlAgentToolType dialect, string sql)
    {
        var analysis = Analyze(sql, dialect);
        var statement = Assert.Single(analysis.Statements);
        var intent = Assert.Single(statement.SecurityIntents);
        Assert.Equal(SqlSecurityAction.Revoke, intent.Action);
        Assert.Equal(SqlTablePrivilege.Update, intent.Privilege);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
    }

    [Theory]
    [InlineData("GRANT ALL ON TABLE users TO analyst")]
    [InlineData("GRANT SELECT(id) ON TABLE users TO analyst")]
    [InlineData("GRANT SELECT ON ALL TABLES IN SCHEMA app TO analyst")]
    [InlineData("GRANT SELECT ON TABLE users TO analyst WITH GRANT OPTION")]
    [InlineData("REVOKE GRANT OPTION FOR SELECT ON TABLE users FROM analyst")]
    [InlineData("REVOKE SELECT ON TABLE users FROM analyst CASCADE")]
    [InlineData("GRANT SELECT ON TABLE users TO analyst GRANTED BY owner")]
    [InlineData("GRANT EXECUTE ON FUNCTION get_users() TO analyst")]
    [InlineData("GRANT SELECT ON TABLE users TO analyst,")]
    public void UnprovenDclGrammarNeverEmitsTrustedOrIncompleteIntents(string sql)
    {
        var analysis = Analyze(sql);
        var statement = Assert.Single(analysis.Statements);
        Assert.NotEqual(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Empty(statement.SecurityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        if (statement.Status == SqlStatementAnalysisStatus.Invalid)
        {
            // Malformed statements do not provide even a potential parsed intent.
            Assert.Empty(statement.DetailedEffects);
        }
        else
        {
            var effect = Assert.Single(statement.DetailedEffects);
            Assert.Equal(SqlStatementEffectOperation.Unknown, effect.Operation);
            Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        }
        Assert.Contains(statement.Diagnostics, d => d.Code.StartsWith("SQL_SECURITY_"));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "GRANT SELECT ON app.users TO analyst")]
    [InlineData(SqlAgentToolType.Sqlite, "GRANT SELECT ON users TO analyst")]
    [InlineData(SqlAgentToolType.MsSqlServer, "GRANT SELECT ON users TO analyst")]
    [InlineData(SqlAgentToolType.Firebird, "GRANT SELECT ON TABLE users TO analyst")]
    [InlineData(SqlAgentToolType.Oracle, "GRANT SELECT ON TABLE users TO analyst")]
    public void UnprovenDialectShapesFailClosed(SqlAgentToolType dialect, string sql)
    {
        var statement = Assert.Single(Analyze(sql, dialect).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.SecurityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Fact]
    public void MySqlExplicitAccountHostIsModeledAsPotentialIntentOnly()
    {
        var analysis = Analyze("GRANT SELECT ON app.users TO 'user'@'localhost'", SqlAgentToolType.MySQL);
        var intent = Assert.Single(analysis.SecurityIntents);
        Assert.Equal(SqlSecurityPrincipalKind.MySqlAccount, intent.PrincipalKind);
        Assert.Equal("user", intent.Principal);
        Assert.Equal("localhost", intent.PrincipalHost);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
        Assert.Null(Assert.Single(analysis.DetailedEffects).ObjectIdentity);
    }

    [Fact]
    public void SecurityIntentCardinalityLimitPreventsCartesianMemoryBlowup()
    {
        var many = string.Join(", ", Enumerable.Range(0, 200).Select(i => "role" + i));
        var statement = Assert.Single(Analyze(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE users TO " + many).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.SecurityIntents);
    }

    [Fact]
    public void DclWithinModeledTransactionCannotMintRollbackOrCatalogProofs()
    {
        var analysis = Analyze("BEGIN; CREATE TABLE users(id INTEGER);" +
            "GRANT SELECT ON TABLE users TO analyst; ROLLBACK; SELECT id FROM users;");
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, analysis.Statements[2].Status);
        Assert.Single(analysis.Statements[2].SecurityIntents);
        Assert.Equal(SqlTransactionState.Unknown, analysis.FinalTransaction);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, analysis.Statements[4].CatalogBinding);
    }

    [Fact]
    public void PostgresSearchPathChangesDoNotPretendToVerifyGrantTargets()
    {
        var analysis = Analyze("SET search_path TO app; GRANT SELECT ON users TO analyst;");
        var intent = Assert.Single(analysis.SecurityIntents);
        Assert.Single(intent.Target.Parts);
        Assert.Equal("users", intent.Target.DisplayName);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
        Assert.Equal("app", analysis.FinalSession.SearchPath!);
    }
}
