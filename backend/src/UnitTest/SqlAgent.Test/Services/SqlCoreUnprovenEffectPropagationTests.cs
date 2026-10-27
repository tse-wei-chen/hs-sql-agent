using System;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// An unsupported/invalid script statement cannot promise that its effect
/// flags exhaust possible side effects. All projections stay non-executable.
/// </summary>
public sealed class SqlCoreUnprovenEffectPropagationTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnsupportedSessionDisclosesUnknownEffectsAcrossAllProviders(SqlAgentToolType dialect)
    {
        var report = Analyze("SET ROLE admin;", dialect);
        var statement = Assert.Single(report.Statements);

        Assert.Equal(SqlScriptStatementKind.Session, statement.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.ChangeSession));
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.True(statement.TransactionProvenance.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(statement.Effects, statement.TransactionProvenance.PotentialEffects);
        Assert.Contains(statement.DetailedEffects,
            effect => effect.Kind.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Equal(SqlEffectCoverage.Unknown, report.EffectCoverage);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("CREATE TABLE bad (id INTEGER DEFAULT 1);", SqlScriptStatementKind.Schema,
        SqlEffectKind.ChangeSchema)]
    [InlineData("GRANT ALL ON users TO reader;", SqlScriptStatementKind.Security,
        SqlEffectKind.ChangePermissions)]
    [InlineData("ROLLBACK;", SqlScriptStatementKind.Transaction,
        SqlEffectKind.ChangeTransaction)]
    [InlineData("UNRECOGNIZED WORDS;", SqlScriptStatementKind.Unknown,
        SqlEffectKind.None)]
    public void UnsupportedFamiliesDiscloseUnknownEffectsToBothConsumers(
        string sql, SqlScriptStatementKind kind, SqlEffectKind expectedFamilyEffect)
    {
        var statement = Assert.Single(Analyze(sql).Statements);

        Assert.Equal(kind, statement.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        if (expectedFamilyEffect == SqlEffectKind.None)
            Assert.Equal(SqlEffectKind.UnknownEffects, statement.Effects);
        else
            Assert.True(statement.Effects.HasFlag(expectedFamilyEffect));
        Assert.Equal(statement.Effects, statement.TransactionProvenance.PotentialEffects);
        Assert.Contains(statement.DetailedEffects,
            effect => effect.Kind.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Theory]
    [InlineData("SELECT FROM users;", SqlScriptStatementKind.Query)]
    [InlineData("CREATE TABLE users(id INTEGER, id TEXT);", SqlScriptStatementKind.Schema)]
    public void InvalidStatementNeverAdvertisesExhaustiveEffects(
        string sql, SqlScriptStatementKind kind)
    {
        var statement = Assert.Single(Analyze(sql).Statements);

        Assert.Equal(kind, statement.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(statement.Effects, statement.TransactionProvenance.PotentialEffects);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.DoesNotContain(statement.DetailedEffects,
            effect => effect.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnknownSessionInsideTransactionPoisonsRollbackAndCarriesEffects()
    {
        var report = Analyze("CREATE TABLE users(id INTEGER); BEGIN;" +
            "SET ROLE admin; ROLLBACK; SELECT id FROM users;");
        var unknown = report.Statements[2];

        Assert.Equal(SqlTransactionState.Active, unknown.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, unknown.TransactionAfter);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, unknown.Status);
        Assert.True(unknown.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.True(unknown.TransactionProvenance.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Contains(unknown.Diagnostics,
            diagnostic => diagnostic.Code == "SQL_TRANSACTION_CONTEXT_UNPROVEN");
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.False(report.FinalSession.IsSearchPathProven);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, report.Statements[4].CatalogBinding);
    }

    [Fact]
    public void KnownStatementsAndModeledPartialIntentsAreNotIndiscriminatelyTainted()
    {
        var report = Analyze("CREATE TABLE users(id INTEGER);" +
            "GRANT SELECT ON users TO reader; SELECT id FROM users;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, report.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, report.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, report.Statements[2].Status);
        Assert.All(report.Statements,
            statement => Assert.False(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects)));
        Assert.True(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ImplicitCommitRetainsTypedDetailWithoutDroppingUnknownEffect()
    {
        var report = Analyze("START TRANSACTION; CREATE TABLE users(id INT);",
            SqlAgentToolType.MySQL);
        var ddl = report.Statements[1];

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, ddl.Status);
        Assert.True(ddl.Effects.HasFlag(SqlEffectKind.ChangeTransaction));
        Assert.True(ddl.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(ddl.Effects, ddl.TransactionProvenance.PotentialEffects);
        Assert.Contains(ddl.DetailedEffects, effect =>
            effect.Operation == SqlStatementEffectOperation.PossibleImplicitCommit);
        Assert.Contains(ddl.DetailedEffects,
            effect => effect.Kind.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.DoesNotContain(ddl.DetailedEffects,
            effect => effect.Certainty == SqlCatalogKnowledge.Known);
    }
}
