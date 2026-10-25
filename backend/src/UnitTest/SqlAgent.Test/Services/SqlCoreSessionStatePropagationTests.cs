using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreSessionStatePropagationTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(), profile);

    private static SqlProviderCapabilityProfile Profile(string path) =>
        new(SqlAgentToolType.Postgres, null, default, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SEARCH_PATH"] = path });

    [Theory]
    [InlineData("SET search_path TO app")]
    [InlineData("SET search_path = app")]
    [InlineData("set SEARCH_PATH to app")]
    public void RecognizedPostgresSetPropagatesToDdlDqlAndDml(string command)
    {
        var result = Analyze(command + "; CREATE TABLE users(id INTEGER); SELECT id FROM users;" +
            "UPDATE users SET id = 2 WHERE id = 1; ALTER TABLE users ADD COLUMN label TEXT;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlEffectKind.ChangeSession, result.Statements[0].Effects);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[0].EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.SetSearchPath,
            Assert.Single(result.Statements[0].DetailedEffects).Operation);
        Assert.Empty(result.Statements[0].Diagnostics);
        Assert.Equal("app", result.Statements[0].SessionAfter.SearchPath);
        Assert.False(result.Statements[0].SessionBefore.IsSearchPathProven);
        Assert.True(result.Statements[1].SessionBefore.IsSearchPathProven);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[3].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Equal(new[] { "app", "users" }, Assert.Single(result.FinalCatalog.Tables).QualifiedName.Parts);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SwitchingSearchPathBindsDifferentPhysicalTablesWithoutPhantomCatalogDeltas()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER, from_app TEXT);" +
            "CREATE TABLE sales.users(id INTEGER, from_sales TEXT);" +
            "SET search_path TO app; SELECT from_app FROM users;" +
            "SET search_path TO sales; SELECT from_sales FROM users;" +
            "SELECT from_app FROM users; SELECT from_app FROM app.users;");
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[3].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[5].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[6].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[7].CatalogBinding);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Empty(result.Statements[4].Changes);
        Assert.Equal(2, result.FinalCatalog.Tables.Count);
        Assert.Equal("sales", result.FinalSession.SearchPath);
    }

    [Fact]
    public void SessionOverridesInitiallyDeclaredSearchPathRatherThanReusingIt()
    {
        var result = Analyze(
            "SET search_path TO sales; CREATE TABLE users(id INTEGER); SELECT id FROM users;",
            Profile("app"));
        Assert.Equal("app", result.Statements[0].SessionBefore.SearchPath);
        Assert.Equal("sales", result.Statements[0].SessionAfter.SearchPath);
        Assert.Equal("sales.users", Assert.Single(result.FinalCatalog.Tables).Name);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
    }

    [Fact]
    public void UnsupportedSessionCommandMustInvalidateStaleSearchPathAndCatalogProof()
    {
        var result = Analyze(
            "SET search_path TO app; CREATE TABLE users(id INTEGER);" +
            "SET search_path TO app, public; SELECT id FROM users;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[2].Status);
        Assert.False(result.Statements[2].SessionAfter.IsSearchPathProven);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[3].CatalogBinding);
        Assert.Equal(SqlEffectCoverage.Unknown, result.Statements[2].EffectCoverage);
        Assert.Contains(result.Statements[2].Diagnostics, d => d.Code == "SQL_SESSION_UNSUPPORTED");
    }

    [Theory]
    [InlineData("SET LOCAL search_path TO app")]
    [InlineData("SET search_path TO \"app\"")]
    [InlineData("SET search_path TO pg_catalog")]
    [InlineData("RESET search_path")]
    [InlineData("SET role TO admin")]
    public void UnsupportedOrConditionalSessionSyntaxNeverClaimsAProvenSearchPath(string command)
    {
        var result = Analyze(command + "; SELECT id FROM users;", Profile("sales"));
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.False(result.FinalSession.IsSearchPathProven);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void TransactionRollbackRestoresSessionScopeAndCatalogSnapshot()
    {
        var result = Analyze(
            "SET search_path TO app; CREATE TABLE users(id INTEGER);" +
            "BEGIN; SET search_path TO sales; CREATE TABLE users(id INTEGER, label TEXT);" +
            "ROLLBACK; SELECT id FROM users;");
        Assert.Equal("sales", result.Statements[4].SessionBefore.SearchPath);
        Assert.Equal("app", result.Statements[5].SessionAfter.SearchPath);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[6].CatalogBinding);
        Assert.Equal("app.users", Assert.Single(result.FinalCatalog.Tables).Name);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
    }

    [Fact]
    public void TransactionCommitKeepsProvenSessionChanges()
    {
        var result = Analyze(
            "BEGIN; SET search_path TO app; CREATE TABLE users(id INTEGER);" +
            "COMMIT; SELECT id FROM users;");
        Assert.Equal("app", result.Statements[3].SessionAfter.SearchPath);
        Assert.Equal("app", result.FinalSession.SearchPath);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[4].CatalogBinding);
    }

    [Fact]
    public void UnknownTransactionCannotRestoreAnEarlierSearchPathProof()
    {
        var result = Analyze(
            "SET search_path TO app; BEGIN; SET search_path TO sales;" +
            "SAVEPOINT s; ROLLBACK; SELECT id FROM users;");
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalSession.IsSearchPathProven);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[5].CatalogBinding);
    }

    [Fact]
    public void OtherProviderDoesNotBorrowPostgresqlSessionSemantics()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SET search_path TO app; CREATE TABLE users(id INTEGER);",
            SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.False(result.FinalSession.IsSearchPathProven);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
