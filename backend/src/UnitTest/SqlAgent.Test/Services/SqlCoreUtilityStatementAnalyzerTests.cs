using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreUtilityStatementAnalyzerTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.MySQL, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Sqlite, "EXPLAIN QUERY PLAN SELECT 1")]
    public void ExplainEstimatedPlansUseTypedPotentialEffectsWithoutExecutionClaims(
        SqlAgentToolType provider, string sql)
    {
        var statement = Assert.Single(Analyze(sql, provider).Statements);
        var intent = Assert.Single(statement.UtilityIntents);
        Assert.Equal(SqlUtilityOperation.ExplainPlan, intent.Operation);
        Assert.Equal(SqlUtilityExecutionMode.PlanningOnly, intent.ExecutionMode);
        Assert.Equal(SqlScriptStatementKind.Query, intent.InnerStatementKind);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
        Assert.Equal(statement.Syntax.Span.Start, intent.Span.Start);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.Administrative));
        Assert.False(statement.Effects.HasFlag(SqlEffectKind.WriteData));
        var effect = Assert.Single(statement.DetailedEffects);
        Assert.Equal(SqlStatementEffectOperation.ExplainPlan, effect.Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        Assert.Null(effect.ObjectIdentity);
        Assert.True(statement.CatalogAfter.IsClosedWorld);
    }

    [Fact]
    public void PostgresExplainAnalyzeQueryExplicitlyModelsActualExecutionAndUnknownSideEffects()
    {
        var analysis = Analyze("CREATE TABLE t(id INTEGER);" +
            "EXPLAIN ANALYZE SELECT id FROM t;" +
            "SELECT id FROM t;");
        var statement = analysis.Statements[1];
        var intent = Assert.Single(statement.UtilityIntents);
        Assert.Equal(SqlUtilityOperation.ExplainExecute, intent.Operation);
        Assert.Equal(SqlUtilityExecutionMode.ExecutesStatement, intent.ExecutionMode);
        Assert.Equal(SqlScriptStatementKind.Query, intent.InnerStatementKind);
        Assert.Equal(SqlStatementEffectOperation.ExplainExecute,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.ReadData));
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.False(statement.CatalogAfter.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, analysis.Statements[2].CatalogBinding);
    }

    [Fact]
    public void PostgresExplainAnalyzeMutationCannotMasqueradeAsReadOnlyPlan()
    {
        var analysis = Analyze(
            "EXPLAIN ANALYZE DELETE FROM orders WHERE id = 1;",
            SqlAgentToolType.Postgres);
        var stmt = Assert.Single(analysis.Statements);
        var intent = Assert.Single(stmt.UtilityIntents);
        Assert.Equal(SqlUtilityOperation.ExplainExecute, intent.Operation);
        Assert.Equal(SqlUtilityExecutionMode.ExecutesStatement, intent.ExecutionMode);
        Assert.Equal(SqlScriptStatementKind.Mutation, intent.InnerStatementKind);
        Assert.True(stmt.Effects.HasFlag(SqlEffectKind.WriteData));
        Assert.True(stmt.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Null(Assert.Single(stmt.DetailedEffects).ObjectIdentity);
    }

    [Theory]
    [InlineData("ANALYZE public.orders", "public.orders")]
    [InlineData("ANALYZE analytics.users", "analytics.users")]
    public void PostgreSqlAnalyzeTableModelsStatsChangesWithoutProvingServerCatalog(
        string sql, string target)
    {
        var analysis = Analyze(sql);
        var statement = Assert.Single(analysis.Statements);
        var intent = Assert.Single(statement.UtilityIntents);
        Assert.Equal(SqlUtilityOperation.AnalyzeStatistics, intent.Operation);
        Assert.Equal(SqlUtilityExecutionMode.Maintenance, intent.ExecutionMode);
        Assert.Equal(target, intent.TargetName);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlStatementEffectOperation.AnalyzeStatistics,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.Null(Assert.Single(statement.DetailedEffects).ObjectIdentity);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Firebird, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Sqlite, "EXPLAIN ANALYZE SELECT 1")]
    [InlineData(SqlAgentToolType.MySQL, "EXPLAIN ANALYZE SELECT 1")]
    [InlineData(SqlAgentToolType.MySQL, "ANALYZE TABLE users")]
    [InlineData(SqlAgentToolType.Sqlite, "ANALYZE users")]
    [InlineData(SqlAgentToolType.Sqlite, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Postgres, "ANALYZE users")]
    [InlineData(SqlAgentToolType.Postgres, "VACUUM")]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN VERBOSE SELECT 1")]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN ANALYZE CREATE TABLE copy AS SELECT 1")]
    public void UnmodeledUtilityAndDialectGrammarFailClosed(SqlAgentToolType provider, string sql)
    {
        var analysis = Analyze(sql, provider);
        var statement = Assert.Single(analysis.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.UtilityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.Unknown,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_UTILITY_UNSUPPORTED");
    }

    [Theory]
    [InlineData("EXPLAIN SELECT FROM users")]
    [InlineData("EXPLAIN ANALYZE SELECT FROM users")]
    public void InvalidWrappedSqlHasNoTrustedOrPotentialModeledIntent(string sql)
    {
        var statement = Assert.Single(Analyze(sql).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Empty(statement.UtilityIntents);
        Assert.Empty(statement.DetailedEffects);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_UTILITY_INNER_INVALID");
    }

    [Fact]
    public void StaticExplainStatementDoesNotProveMissingTableOrQueryPermissions()
    {
        var result = Analyze("EXPLAIN SELECT id FROM private_data;" +
            "SELECT id FROM private_data;");
        Assert.Equal(SqlUtilityExecutionMode.PlanningOnly,
            Assert.Single(result.Statements[0].UtilityIntents).ExecutionMode);
        Assert.Empty(result.Statements[0].Changes);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[1].CatalogBinding);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void EffectSummaryFlatteningDoesNotAllocateUnknownIntentForUnsupportedUtilities()
    {
        var result = Analyze("EXPLAIN SELECT 1; VACUUM; ANALYZE public.stats;");
        Assert.Equal(3, result.Statements.Count);
        Assert.Equal(2, result.UtilityIntents.Count);
        Assert.Empty(result.Statements[1].UtilityIntents);
        Assert.Equal(SqlUtilityOperation.ExplainPlan, result.UtilityIntents[0].Operation);
        Assert.Equal(SqlUtilityOperation.AnalyzeStatistics, result.UtilityIntents[1].Operation);
        Assert.All(result.UtilityIntents, intent =>
            Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty));
    }


    [Theory]
    [InlineData("EXPLAIN (ANALYZE TRUE) SELECT 1", SqlUtilityExecutionMode.ExecutesStatement)]
    [InlineData("EXPLAIN (ANALYZE FALSE) SELECT 1", SqlUtilityExecutionMode.PlanningOnly)]
    public void ExplicitPostgresAnalyzeOptionDoesNotLoseExecutionDistinction(
        string sql, SqlUtilityExecutionMode mode)
    {
        var statement = Assert.Single(Analyze(sql).Statements);
        var intent = Assert.Single(statement.UtilityIntents);
        Assert.Equal(mode, intent.ExecutionMode);
        Assert.Equal(mode == SqlUtilityExecutionMode.ExecutesStatement,
            intent.MayExecuteStatement);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
    }

    [Fact]
    public void PostgreSqlVacuumNamedTableIsMaintenanceNotExplainDryRun()
    {
        var statement = Assert.Single(Analyze("VACUUM public.orders").Statements);
        var intent = Assert.Single(statement.UtilityIntents);
        Assert.Equal(SqlUtilityOperation.VacuumMaintenance, intent.Operation);
        Assert.Equal(SqlUtilityExecutionMode.Maintenance, intent.ExecutionMode);
        Assert.Equal("public.orders", intent.TargetName);
        Assert.False(statement.CatalogAfter.IsClosedWorld);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Null(Assert.Single(statement.DetailedEffects).ObjectIdentity);
    }

    [Fact]
    public void ExplainAnalyzeInsideTransactionInvalidatesRollbackCatalogProof()
    {
        var result = Analyze(
            "BEGIN; CREATE TABLE t(id INTEGER);" +
            "EXPLAIN ANALYZE SELECT id FROM t; ROLLBACK;");
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[3].Status);
    }
}
