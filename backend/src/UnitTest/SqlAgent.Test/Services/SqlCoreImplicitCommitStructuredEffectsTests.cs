using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Static DDL transaction-risk flags must not disappear when a consumer reads
/// the typed DetailedEffects projection. No effect proves server execution.
/// </summary>
public sealed class SqlCoreImplicitCommitStructuredEffectsTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType dialect) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "CREATE TABLE users(id INTEGER);")]
    [InlineData(SqlAgentToolType.Oracle, "CREATE TABLE users(id NUMBER);")]
    public void ProvenDdlExposesPossibleImplicitCommitInDetailedEffects(
        SqlAgentToolType dialect, string sql)
    {
        var report = Analyze(sql, dialect);
        var ddl = Assert.Single(report.Statements);

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            ddl.TransactionProvenance.Origin);
        Assert.True(ddl.Effects.HasFlag(SqlEffectKind.ChangeTransaction));

        var schema = Assert.Single(ddl.DetailedEffects.Where(e =>
            e.Kind == SqlEffectKind.ChangeSchema));
        Assert.Equal(SqlStatementEffectOperation.CreateTable, schema.Operation);
        Assert.Equal(SqlCatalogKnowledge.Known, schema.Certainty);

        var possibleCommit = Assert.Single(ddl.DetailedEffects.Where(e =>
            e.Kind == SqlEffectKind.ChangeTransaction));
        Assert.Equal(SqlCatalogKnowledge.Unknown, possibleCommit.Certainty);
        Assert.Equal(SqlStatementEffectOperation.PossibleImplicitCommit, possibleCommit.Operation);
        Assert.Null(possibleCommit.ObjectName);
        Assert.Null(possibleCommit.ObjectIdentity);
        Assert.Empty(possibleCommit.Dependencies);
        Assert.Equal(ddl.Syntax.Span.Start, possibleCommit.Span.Start);
        Assert.Equal(ddl.Syntax.Span.Length, possibleCommit.Span.Length);
        Assert.Contains(report.DetailedEffects, e => ReferenceEquals(e, possibleCommit));
    }

    [Fact]
    public void MySqlDdlInActiveTransactionRetainsUnknownImplicitCommitEffectWithoutCatalogProof()
    {
        var report = Analyze(
            "START TRANSACTION; CREATE TABLE users(id INTEGER); ROLLBACK;",
            SqlAgentToolType.MySQL);
        var ddl = report.Statements[1];

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, ddl.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, ddl.EffectCoverage);
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            ddl.TransactionProvenance.Origin);
        Assert.Contains(ddl.Diagnostics,
            d => d.Code == "SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN");
        Assert.Empty(ddl.Changes);

        var possibleCommit = Assert.Single(ddl.DetailedEffects.Where(e =>
            e.Kind == SqlEffectKind.ChangeTransaction));
        Assert.Equal(SqlCatalogKnowledge.Unknown, possibleCommit.Certainty);
        Assert.Equal(SqlStatementEffectOperation.PossibleImplicitCommit, possibleCommit.Operation);
        Assert.DoesNotContain(ddl.DetailedEffects, e =>
            e.Kind == SqlEffectKind.ChangeSchema &&
            e.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherProviderDdlDoesNotInventImplicitCommitDetails(SqlAgentToolType dialect)
    {
        var ddl = Assert.Single(Analyze("CREATE TABLE users(id INTEGER);", dialect).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.False(ddl.Effects.HasFlag(SqlEffectKind.ChangeTransaction));
        Assert.DoesNotContain(ddl.DetailedEffects,
            e => e.Kind == SqlEffectKind.ChangeTransaction);
    }

    [Fact]
    public void MySqlConditionalNoOpDdlStillDisclosesPossibleCommit()
    {
        var report = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE TABLE IF NOT EXISTS users(other INTEGER);",
            SqlAgentToolType.MySQL);
        var ddl = report.Statements[1];

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, ddl.Status);
        Assert.Empty(ddl.Changes);
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            ddl.TransactionProvenance.Origin);
        var possibleCommit = Assert.Single(ddl.DetailedEffects);
        Assert.Equal(SqlEffectKind.ChangeTransaction, possibleCommit.Kind);
        Assert.Equal(SqlStatementEffectOperation.PossibleImplicitCommit, possibleCommit.Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown, possibleCommit.Certainty);
    }
}
