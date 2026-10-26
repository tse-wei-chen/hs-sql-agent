using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Static procedure syntax is never a proof of execution, effects, or authorization.</summary>
public sealed class SqlCoreProceduralIntentTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType dialect) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "CALL app.refresh_cache()", "app.refresh_cache", SqlProceduralInvocationKind.Call)]
    [InlineData(SqlAgentToolType.MySQL, "CALL sales.reconcile()", "sales.reconcile", SqlProceduralInvocationKind.Call)]
    [InlineData(SqlAgentToolType.MySQL, "CALL sales.reconcile", "sales.reconcile", SqlProceduralInvocationKind.Call)]
    [InlineData(SqlAgentToolType.Oracle, "CALL APP.REFRESH_CACHE()", "APP.REFRESH_CACHE", SqlProceduralInvocationKind.Call)]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXEC dbo.refresh_cache", "DBO.REFRESH_CACHE", SqlProceduralInvocationKind.Exec)]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXECUTE dbo.refresh_cache", "DBO.REFRESH_CACHE", SqlProceduralInvocationKind.Exec)]
    [InlineData(SqlAgentToolType.Firebird, "EXECUTE PROCEDURE REFRESH_CACHE", "REFRESH_CACHE", SqlProceduralInvocationKind.ExecuteProcedure)]
    public void StaticZeroArgumentInvocationsCarryOpaquePotentialEffects(
        SqlAgentToolType provider, string sql, string name, SqlProceduralInvocationKind expectedKind)
    {
        var analysis = Analyze(sql + ";", provider);
        var statement = Assert.Single(analysis.Statements);
        var intent = Assert.Single(analysis.ProceduralIntents);
        Assert.Same(intent, Assert.Single(statement.ProceduralIntents));
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlScriptStatementKind.Procedural, statement.Kind);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Equal(expectedKind, intent.InvocationKind);
        Assert.Equal(name, intent.Routine.DisplayName);
        Assert.False(intent.BodyAnalyzed);
        Assert.True(intent.MayExecuteSql);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.RuntimeCertainty);
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.WriteData));
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.ChangeSchema));
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.ChangePermissions));
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.ChangeTransaction));
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.ChangeSession));
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(SqlStatementEffectOperation.InvokeProcedure,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.Null(statement.DetailedEffects[0].ObjectIdentity);
        Assert.Equal(SqlCatalogKnowledge.Unknown, statement.DetailedEffects[0].Certainty);
        Assert.Empty(statement.Changes);
        Assert.Empty(statement.Diagnostics);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SeveralCallsRemainOrderedAndDoNotRestoreStaleCatalogProof()
    {
        var report = Analyze("CALL app.a(); CALL app.b();", SqlAgentToolType.Postgres);
        Assert.Equal(2, report.ProceduralIntents.Count);
        Assert.Equal("app.a", report.ProceduralIntents[0].Routine.DisplayName);
        Assert.Equal("app.b", report.ProceduralIntents[1].Routine.DisplayName);
        Assert.Equal(SqlStatementEffectOperation.InvokeProcedure, report.DetailedEffects[0].Operation);
        Assert.Equal(SqlStatementEffectOperation.InvokeProcedure, report.DetailedEffects[1].Operation);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "CALL unqualified()")]
    [InlineData(SqlAgentToolType.Postgres, "CALL app.a(1)")]
    [InlineData(SqlAgentToolType.Postgres, "CALL app.a('x')")]
    [InlineData(SqlAgentToolType.Postgres, "CALL app.a() WITH GRANT OPTION")]
    [InlineData(SqlAgentToolType.MySQL, "CALL unqualified()")]
    [InlineData(SqlAgentToolType.MySQL, "CALL app.a(1)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXEC unqualified")]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXEC dbo.a 1")]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXEC dbo.a WITH RECOMPILE")]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXEC('DELETE FROM users')")]
    [InlineData(SqlAgentToolType.Firebird, "EXECUTE PROCEDURE a(1)")]
    [InlineData(SqlAgentToolType.Firebird, "EXECUTE STATEMENT 'DELETE FROM users'")]
    [InlineData(SqlAgentToolType.Oracle, "CALL a()")]
    [InlineData(SqlAgentToolType.Sqlite, "CALL app.a()")]
    public void UnprovenInvocationFormsNeverEmitTrustedOrPartialIntents(
        SqlAgentToolType provider, string sql)
    {
        var report = Analyze(sql, provider);
        var statement = Assert.Single(report.Statements);
        Assert.Empty(report.ProceduralIntents);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.Unknown,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.True(report.FinalCatalog.IsClosedWorld == false);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_PROCEDURAL_UNSUPPORTED");
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "CREATE OR ALTER PROCEDURE dbo.do_work AS BEGIN SELECT 1; DELETE FROM users; END; SELECT 1;")]
    [InlineData(SqlAgentToolType.Firebird, "EXECUTE BLOCK AS BEGIN DELETE FROM users; DELETE FROM logs; END; SELECT 1;")]
    [InlineData(SqlAgentToolType.Oracle, "BEGIN DELETE FROM users; UPDATE users SET id=1; END; SELECT 1;")]
    public void OpaqueBodiesCannotBeSplitIntoFalselyVerifiedNestedStatements(
        SqlAgentToolType provider, string sql)
    {
        var report = Analyze(sql, provider);
        var statement = Assert.Single(report.Statements);
        Assert.Equal(SqlScriptStatementKind.Procedural, statement.Kind);
        Assert.Empty(report.ProceduralIntents);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_PROCEDURAL_UNSUPPORTED");
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void PostgreSqlOpaqueSqlProcedureBodyIsNotSplitButFollowingQueryIsIndependent()
    {
        var result = Analyze(
            "CREATE PROCEDURE app.do_work() LANGUAGE SQL AS $ BEGIN UPDATE users SET id=1; DELETE FROM users; END $; SELECT 1;",
            SqlAgentToolType.Postgres);
        Assert.Equal(2, result.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Procedural, result.Statements[0].Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.Empty(result.ProceduralDefinitions);
        Assert.Equal(SqlScriptStatementKind.Query, result.Statements[1].Kind);
        Assert.Contains(result.Statements[0].Diagnostics, d =>
            d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void FirebirdUnboundBlockVariablesRemainRejectedAtLexicalBoundary()
    {
        // This is not an implicit PSQL variable binding feature:
        // the script lexer and execution compiler still reject :variables.
        Assert.Throws<HsSqlAgent.SqlCore.SqlParsing.SqlParseException>(() =>
            Analyze("EXECUTE BLOCK AS BEGIN SELECT 1 INTO :id; END;",
                SqlAgentToolType.Firebird));
    }

    [Fact]
    public void SqlServerGoBatchCanSeparateOpaqueDefinitionFromFollowingStatement()
    {
        var report = Analyze(
            "CREATE PROCEDURE dbo.work AS BEGIN DELETE FROM users; END\nGO\nSELECT 1;",
            SqlAgentToolType.MsSqlServer);
        Assert.Equal(2, report.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Procedural, report.Statements[0].Kind);
        Assert.Equal(SqlScriptStatementKind.Query, report.Statements[1].Kind);
        Assert.Empty(report.ProceduralIntents);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, report.Statements[0].Status);
    }

    [Fact]
    public void InvokingStoredProcedureInTransactionAlwaysInvalidatesRollbackProof()
    {
        var report = Analyze(
            "BEGIN; CALL app.do_work(); ROLLBACK;", SqlAgentToolType.Postgres);
        Assert.Equal(SqlTransactionState.Active, report.Statements[1].TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[1].TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
            report.Statements[1].TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven,
            report.Statements[1].TransactionProvenance.RollbackScope);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }
}
