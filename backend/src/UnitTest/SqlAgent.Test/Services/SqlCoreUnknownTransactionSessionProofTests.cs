using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Recognizing a SET/USE command in a script with unknown transaction
/// state is not proof that the command took effect on a live connection.
/// </summary>
public sealed class SqlCoreUnknownTransactionSessionProofTests
{
    private static SqlScriptAnalysisResult Analyze(
        string sql, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Fact]
    public void PostgresSetAfterUnknownBoundaryCannotReestablishSearchPath()
    {
        var report = Analyze(
            "DISCARD ALL; SET search_path TO app; CREATE TABLE users(id INTEGER);" +
            "SELECT id FROM users;");
        var setting = report.Statements[1];

        Assert.Equal(SqlTransactionState.Unknown, setting.TransactionBefore);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, setting.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, setting.Certainty);
        Assert.Equal(SqlTransactionState.Unknown, setting.TransactionAfter);
        Assert.Equal(SqlStatementEffectOperation.SetSearchPath,
            Assert.Single(setting.DetailedEffects).Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            Assert.Single(setting.DetailedEffects).Certainty);
        Assert.True(setting.Effects.HasFlag(SqlEffectKind.ChangeSession));
        Assert.True(setting.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(SqlEffectCoverage.Partial, setting.EffectCoverage);
        Assert.Equal(SqlTransactionEffectOrigin.UnknownTransactionState,
            setting.TransactionProvenance.Origin);
        Assert.Null(setting.SessionAfter.SearchPath);
        Assert.False(setting.SessionAfter.IsSearchPathProven);
        Assert.Empty(setting.Diagnostics);
        Assert.False(report.FinalSession.IsSearchPathProven);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified,
            report.Statements[3].CatalogBinding);
    }

    [Fact]
    public void UnknownActiveTransactionCannotMintNewSearchPathProof()
    {
        var report = Analyze("BEGIN; SET ROLE admin; SET search_path TO app;");

        Assert.Equal(SqlTransactionState.Active, report.Statements[1].TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[1].TransactionAfter);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[2].TransactionBefore);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed,
            report.Statements[2].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, report.Statements[2].Certainty);
        Assert.Null(report.Statements[2].SessionAfter.SearchPath);
        Assert.False(report.FinalSession.IsSearchPathProven);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "SET NOCOUNT ON;",
        SqlStatementEffectOperation.SetNoCount)]
    [InlineData(SqlAgentToolType.MsSqlServer, "USE archive;",
        SqlStatementEffectOperation.UseDatabase)]
    [InlineData(SqlAgentToolType.MySQL, "USE archive;",
        SqlStatementEffectOperation.UseDatabase)]
    public void SupportedSqlServerAndMySqlSessionSyntaxAfterUnknownCannotMintSessionFacts(
        SqlAgentToolType dialect, string command, SqlStatementEffectOperation operation)
    {
        var report = Analyze("DISCARD ALL; " + command, dialect);
        var setting = report.Statements[1];

        Assert.Equal(SqlTransactionState.Unknown, setting.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, setting.TransactionAfter);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, setting.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, setting.Certainty);
        Assert.Equal(operation, Assert.Single(setting.DetailedEffects).Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            Assert.Single(setting.DetailedEffects).Certainty);
        Assert.True(setting.Effects.HasFlag(SqlEffectKind.ChangeSession));
        Assert.True(setting.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Null(setting.SessionAfter.CurrentDatabase);
        Assert.False(setting.SessionAfter.IsDatabaseScopeProven);
        Assert.Null(setting.SessionAfter.NoCount);
        Assert.Null(report.FinalSession.CurrentDatabase);
        Assert.Null(report.FinalSession.NoCount);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
        Assert.Empty(setting.Diagnostics);
    }

    [Fact]
    public void SqlServerUseCannotRepairUnknownContextFromUnsupportedDdl()
    {
        var report = Analyze("CREATE TABLE dbo.users(id INTEGER); USE archive;",
            SqlAgentToolType.MsSqlServer);

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, report.Statements[0].Status);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[0].TransactionAfter);
        var setting = report.Statements[1];
        Assert.Equal(SqlTransactionState.Unknown, setting.TransactionBefore);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, setting.Status);
        Assert.Equal(SqlStatementEffectOperation.UseDatabase,
            Assert.Single(setting.DetailedEffects).Operation);
        Assert.Null(setting.SessionAfter.CurrentDatabase);
        Assert.False(setting.SessionAfter.IsDatabaseScopeProven);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void KnownSessionProofMustNotReturnAfterSeveralValidCommands()
    {
        var report = Analyze(
            "SET search_path TO app; DISCARD ALL;" +
            "SET search_path TO sales; SET search_path TO archive;");
        Assert.Equal("app", report.Statements[0].SessionAfter.SearchPath);
        Assert.Null(report.Statements[1].SessionAfter.SearchPath);
        Assert.Null(report.Statements[2].SessionAfter.SearchPath);
        Assert.Null(report.Statements[3].SessionAfter.SearchPath);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
        Assert.False(report.FinalSession.IsSearchPathProven);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed,
            report.Statements[3].Status);
    }

    [Fact]
    public void UnknownTransactionStillKeepsPreciseUnsupportedSessionDiagnostic()
    {
        var report = Analyze("DISCARD ALL; SET ROLE admin;");
        var setting = report.Statements[1];

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, setting.Status);
        Assert.True(setting.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Contains(setting.Diagnostics,
            diagnostic => diagnostic.Code == "SQL_SESSION_UNSUPPORTED");
        Assert.DoesNotContain(setting.Diagnostics,
            diagnostic => diagnostic.Code == "SQL_TRANSACTION_CONTEXT_UNPROVEN");
        Assert.False(report.FinalSession.IsSearchPathProven);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SET search_path TO app;",
        SqlStatementEffectOperation.SetSearchPath)]
    [InlineData(SqlAgentToolType.MsSqlServer, "SET NOCOUNT ON;",
        SqlStatementEffectOperation.SetNoCount)]
    public void KnownOutsideContextStillYieldsKnownSessionProof(
        SqlAgentToolType dialect, string command, SqlStatementEffectOperation operation)
    {
        var statement = Assert.Single(Analyze(command, dialect).Statements);

        Assert.Equal(SqlTransactionState.Outside, statement.TransactionBefore);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, statement.Certainty);
        Assert.Equal(operation, Assert.Single(statement.DetailedEffects).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(statement.DetailedEffects).Certainty);
        Assert.Equal(SqlTransactionState.Outside, statement.TransactionAfter);
        Assert.Equal(SqlEffectCoverage.Complete, statement.EffectCoverage);
        Assert.False(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
    }

    [Fact]
    public void ModeledActivePostgresSessionCanStillRollbackToOriginalScope()
    {
        var report = Analyze(
            "SET search_path TO app; BEGIN; SET search_path TO sales; ROLLBACK;");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, report.Statements[2].Status);
        Assert.Equal("sales", report.Statements[2].SessionAfter.SearchPath);
        Assert.Equal("app", report.FinalSession.SearchPath);
        Assert.Equal(SqlTransactionState.Outside, report.FinalTransaction);
        Assert.Empty(report.Diagnostics);
    }
}
