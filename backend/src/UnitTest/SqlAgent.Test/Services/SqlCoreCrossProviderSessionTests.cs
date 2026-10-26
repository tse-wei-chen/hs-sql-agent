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

/// <summary>
/// Analyzed session premises are NOT server context attestation,
/// a live database existence proof, or permission to execute.
/// </summary>
public sealed class SqlCoreCrossProviderSessionTests
{
    private static SqlProviderCapabilityProfile MySqlProfile() =>
        new(SqlAgentToolType.MySQL, null, default, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["LOWER_CASE_TABLE_NAMES"] = "1" });

    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType dialect,
        SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty(), profile);

    [Fact]
    public void MySqlUsePropagatesDefaultDatabaseAcrossDdlAndQuery()
    {
        var analysis = Analyze("USE sales; CREATE TABLE users(id INTEGER); SELECT id FROM users;",
            SqlAgentToolType.MySQL, MySqlProfile());
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[0].Status);
        Assert.Equal("SALES", analysis.Statements[0].SessionAfter.CurrentDatabase);
        Assert.True(analysis.Statements[0].SessionAfter.IsDatabaseScopeProven);
        Assert.Equal(SqlStatementEffectOperation.UseDatabase,
            Assert.Single(analysis.Statements[0].DetailedEffects).Operation);
        Assert.Equal(SqlEffectCoverage.Complete, analysis.Statements[0].EffectCoverage);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[1].Status);
        Assert.Equal(new[] { "SALES", "USERS" },
            Assert.Single(analysis.FinalCatalog.Tables).QualifiedName.Parts);
        Assert.Equal(SqlCatalogBindingStatus.Verified, analysis.Statements[2].CatalogBinding);
        Assert.True(analysis.FinalCatalog.IsClosedWorld);
        Assert.Null(analysis.FinalSession.SearchPath);
    }

    [Fact]
    public void MySqlUseSwitchesResolutionScopeWithoutCreatingPhantomTables()
    {
        var analysis = Analyze(
            "CREATE TABLE sales.users(id INTEGER, sales_id INTEGER);" +
            "CREATE TABLE archive.users(id INTEGER, archive_id INTEGER);" +
            "USE sales; SELECT sales_id FROM users;" +
            "USE archive; SELECT archive_id FROM users;" +
            "SELECT sales_id FROM users; SELECT sales_id FROM sales.users;",
            SqlAgentToolType.MySQL, MySqlProfile());
        Assert.Equal(SqlCatalogBindingStatus.Verified, analysis.Statements[3].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, analysis.Statements[5].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, analysis.Statements[6].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, analysis.Statements[7].CatalogBinding);
        Assert.Equal(2, analysis.FinalCatalog.Tables.Count);
        Assert.All(new[] { analysis.Statements[2], analysis.Statements[4] },
            statement => Assert.Empty(statement.Changes));
        Assert.Equal("ARCHIVE", analysis.FinalSession.CurrentDatabase);
    }

    [Fact]
    public void MySqlUseWithoutCaseRulesNeverCreatesFalsePhysicalBindingProof()
    {
        var analysis = Analyze("CREATE TABLE sales.users(id INTEGER);" +
            "USE sales; SELECT id FROM users;", SqlAgentToolType.MySQL);
        Assert.Equal("sales", analysis.Statements[1].SessionAfter.CurrentDatabase);
        Assert.False(analysis.Statements[1].SessionAfter.IsDatabaseScopeProven);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, analysis.Statements[2].CatalogBinding);
        Assert.Equal(SqlEffectCoverage.Partial, analysis.Statements[1].EffectCoverage);
    }

    [Fact]
    public void SqlServerUseChangesDatabaseContextButMustNotPretendToProveDefaultSchema()
    {
        var result = Analyze("CREATE TABLE dbo.users(id INTEGER);" +
            "USE archive; SELECT id FROM dbo.users;",
            SqlAgentToolType.MsSqlServer);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, result.Statements[1].Status);
        Assert.Equal("ARCHIVE", result.Statements[1].SessionAfter.CurrentDatabase);
        Assert.False(result.Statements[1].SessionAfter.IsDatabaseScopeProven);
        Assert.Equal(SqlStatementEffectOperation.UseDatabase,
            Assert.Single(result.Statements[1].DetailedEffects).Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            Assert.Single(result.Statements[1].DetailedEffects).Certainty);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[2].CatalogBinding);
    }

    [Fact]
    public void SqlServerNoCountIsTypedAndDoesNotInvalidateExistingCatalog()
    {
        var analysis = Analyze(
            "CREATE TABLE dbo.users(id INTEGER); SET NOCOUNT ON; SET NOCOUNT OFF;",
            SqlAgentToolType.MsSqlServer);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[1].Status);
        Assert.Equal(true, analysis.Statements[1].SessionAfter.NoCount);
        Assert.Equal(false, analysis.Statements[2].SessionAfter.NoCount);
        Assert.Null(analysis.Statements[0].SessionBefore.NoCount);
        Assert.Equal(SqlStatementEffectOperation.SetNoCount,
            Assert.Single(analysis.Statements[1].DetailedEffects).Operation);
        Assert.Equal(SqlEffectCoverage.Complete, analysis.Statements[2].EffectCoverage);
        Assert.True(analysis.FinalCatalog.IsClosedWorld);
        Assert.Single(analysis.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "USE sales")]
    [InlineData(SqlAgentToolType.MsSqlServer, "USE sales")]
    public void DatabaseContextPersistsToNextSessionSnapshot(SqlAgentToolType provider, string sql)
    {
        var result = Analyze(sql + "; SELECT 1;", provider,
            provider == SqlAgentToolType.MySQL ? MySqlProfile() : null);
        Assert.Equal(result.Statements[0].SessionAfter.CurrentDatabase,
            result.Statements[1].SessionBefore.CurrentDatabase);
        Assert.Equal(SqlStatementEffectOperation.UseDatabase,
            Assert.Single(result.Statements[0].DetailedEffects).Operation);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "USE sales")]
    [InlineData(SqlAgentToolType.Oracle, "USE sales")]
    [InlineData(SqlAgentToolType.Sqlite, "USE sales")]
    [InlineData(SqlAgentToolType.Firebird, "USE sales")]
    [InlineData(SqlAgentToolType.Postgres, "SET NOCOUNT ON")]
    [InlineData(SqlAgentToolType.MySQL, "SET NOCOUNT ON")]
    [InlineData(SqlAgentToolType.Oracle, "SET NOCOUNT ON")]
    public void DialectSpecificSessionOperationsNeverLeak(SqlAgentToolType provider, string sql)
    {
        var statement = Assert.Single(Analyze(sql, provider).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.Unknown,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.False(statement.CatalogAfter.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "USE sales, archive")]
    [InlineData(SqlAgentToolType.MySQL, "USE 'sales'")]
    [InlineData(SqlAgentToolType.MySQL, "USE `sales`")]
    [InlineData(SqlAgentToolType.MsSqlServer, "USE sales, archive")]
    [InlineData(SqlAgentToolType.MsSqlServer, "USE [sales]")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SET NOCOUNT ON, QUOTED_IDENTIFIER OFF")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SET NOCOUNT 1")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SET QUOTED_IDENTIFIER OFF")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SET XACT_ABORT OFF")]
    public void UnsupportedOrConditionalSessionSyntaxMustDiscardProofs(SqlAgentToolType provider, string sql)
    {
        var statement = Assert.Single(Analyze(sql, provider).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.False(statement.CatalogAfter.IsClosedWorld);
        Assert.Null(statement.SessionAfter.CurrentDatabase);
        Assert.Null(statement.SessionAfter.NoCount);
    }

    [Fact]
    public void UnknownSessionSettingInvalidatesSqlServerNoCountSnapshot()
    {
        var analysis = Analyze("SET NOCOUNT ON; SET QUOTED_IDENTIFIER OFF;", SqlAgentToolType.MsSqlServer);
        Assert.Equal(true, analysis.Statements[1].SessionBefore.NoCount);
        Assert.Null(analysis.FinalSession.NoCount);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, analysis.Statements[1].Status);
    }

    [Fact]
    public void PostgresSearchPathAndRollbackStillWorkExactlyAsBefore()
    {
        var analysis = Analyze("SET search_path TO app; BEGIN; SET search_path TO sales;" +
            "ROLLBACK; CREATE TABLE users(id INTEGER); SELECT id FROM users;",
            SqlAgentToolType.Postgres);
        Assert.Equal("app", analysis.FinalSession.SearchPath);
        Assert.Null(analysis.FinalSession.CurrentDatabase);
        Assert.Null(analysis.FinalSession.NoCount);
        Assert.Equal(SqlCatalogBindingStatus.Verified, analysis.Statements[5].CatalogBinding);
        Assert.Equal("app.users", Assert.Single(analysis.FinalCatalog.Tables).Name);
    }

    [Fact]
    public void MySqlUnmodeledTransactionCannotRestorePriorDatabaseScope()
    {
        var analysis = Analyze("USE sales; BEGIN; USE archive; ROLLBACK;",
            SqlAgentToolType.MySQL, MySqlProfile());
        Assert.NotEqual(SqlTransactionState.Active, analysis.FinalTransaction);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Null(analysis.FinalSession.CurrentDatabase);
    }
}
