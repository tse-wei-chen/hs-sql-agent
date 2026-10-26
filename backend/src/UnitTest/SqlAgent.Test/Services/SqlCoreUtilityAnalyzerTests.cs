using System;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Utility syntax and potential effects, never execution or approval.</summary>
public sealed class SqlCoreUtilityAnalyzerTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.MySQL, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Sqlite, "EXPLAIN QUERY PLAN SELECT 1")]
    public void PlanOnlyExplainIsTypedAndNeverSynthesizesExecution(SqlAgentToolType provider, string sql)
    {
        var result = Analyze(sql, provider);
        var statement = Assert.Single(result.Statements);
        var intent = Assert.Single(statement.UtilityIntents);
        Assert.Equal(SqlUtilityOperation.ExplainPlan, intent.Operation);
        Assert.Equal(SqlUtilityExecutionMode.PlanningOnly, intent.ExecutionMode);
        Assert.Equal(SqlCatalogKnowledge.Unknown, intent.Certainty);
        Assert.Equal(SqlScriptStatementKind.Query, intent.InnerStatementKind);
        Assert.False(intent.MayExecuteStatement);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.ExplainPlan, Assert.Single(statement.DetailedEffects).Operation);
        Assert.Equal(SqlEffectKind.Administrative, statement.Effects);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN ANALYZE SELECT 1")]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN (ANALYZE TRUE) SELECT 1")]
    [InlineData(SqlAgentToolType.MySQL, "EXPLAIN ANALYZE SELECT 1")]
    public void ExplainAnalyzeMustAdvertiseRealExecution(SqlAgentToolType provider, string sql)
    {
        var result = Analyze(sql, provider);
        var intent = Assert.Single(result.UtilityIntents);
        Assert.Equal(SqlUtilityOperation.ExplainExecute, intent.Operation);
        Assert.True(intent.MayExecuteStatement);
        Assert.Equal(SqlUtilityExecutionMode.ExecutesStatement, intent.ExecutionMode);
        Assert.Equal(SqlScriptStatementKind.Query, intent.InnerStatementKind);
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.ReadData));
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(SqlEffectCoverage.Partial, result.Statements[0].EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.ExplainExecute, Assert.Single(result.DetailedEffects).Operation);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN ANALYZE UPDATE orders SET id = 1 WHERE id = 2")]
    [InlineData(SqlAgentToolType.Postgres, "EXPLAIN ANALYZE DELETE FROM orders WHERE id = 2")]
    public void ExplainAnalyzeDmlCanWriteAndInvalidatesCatalogProof(SqlAgentToolType provider, string sql)
    {
        var result = Analyze(sql, provider);
        var intent = Assert.Single(result.UtilityIntents);
        Assert.Equal(SqlScriptStatementKind.Mutation, intent.InnerStatementKind);
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.WriteData));
        Assert.True(intent.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlStatementEffectOperation.ExplainExecute, Assert.Single(result.DetailedEffects).Operation);
    }

    [Fact]
    public void ExplainWithoutAnalyzeDoesNotRunUnderlyingDml()
    {
        var result = Analyze("EXPLAIN DELETE FROM orders WHERE id = 1;");
        var intent = Assert.Single(result.UtilityIntents);
        Assert.Equal(SqlScriptStatementKind.Mutation, intent.InnerStatementKind);
        Assert.Equal(SqlUtilityExecutionMode.PlanningOnly, intent.ExecutionMode);
        Assert.False(intent.PotentialEffects.HasFlag(SqlEffectKind.WriteData));
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void PostgresAnalyzeAndVacuumHaveDistinctMaintenanceEffects()
    {
        var result = Analyze("ANALYZE app.orders; VACUUM app.orders;");
        Assert.Equal(2, result.UtilityIntents.Count);
        Assert.Equal(SqlUtilityOperation.AnalyzeStatistics, result.UtilityIntents[0].Operation);
        Assert.Equal(SqlUtilityOperation.VacuumMaintenance, result.UtilityIntents[1].Operation);
        Assert.Equal("app.orders", result.UtilityIntents[0].TargetName);
        Assert.Equal("app.orders", result.UtilityIntents[1].TargetName);
        Assert.All(result.UtilityIntents, i =>
        {
            Assert.Equal(SqlUtilityExecutionMode.Maintenance, i.ExecutionMode);
            Assert.True(i.PotentialEffects.HasFlag(SqlEffectKind.Administrative));
            Assert.True(i.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
            Assert.Equal(SqlCatalogKnowledge.Unknown, i.Certainty);
        });
        Assert.Equal(SqlStatementEffectOperation.AnalyzeStatistics, result.DetailedEffects[0].Operation);
        Assert.Equal(SqlStatementEffectOperation.VacuumMaintenance, result.DetailedEffects[1].Operation);
        Assert.All(result.DetailedEffects, e => Assert.Null(e.ObjectIdentity));
        Assert.Empty(result.Diagnostics);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("EXPLAIN (ANALYZE FALSE) SELECT 1")]
    [InlineData("EXPLAIN (ANALYZE OFF) SELECT 1")]
    public void ExplicitAnalyzeFalseRemainsPlanOnly(string sql)
    {
        var intent = Assert.Single(Analyze(sql).UtilityIntents);
        Assert.False(intent.MayExecuteStatement);
        Assert.Equal(SqlUtilityOperation.ExplainPlan, intent.Operation);
    }

    [Theory]
    [InlineData("EXPLAIN ANALYZE INSERT INTO orders(id) VALUES (1)")]
    [InlineData("EXPLAIN (BUFFERS TRUE) SELECT 1")]
    [InlineData("EXPLAIN EXECUTE dangerous_proc")]
    [InlineData("EXPLAIN (ANALYZE TRUE, FORMAT JSON) SELECT 1")]
    [InlineData("VACUUM FULL app.orders")]
    [InlineData("VACUUM")]
    [InlineData("ANALYZE")]
    [InlineData("ANALYZE app.orders(id)")]
    [InlineData("PRAGMA journal_mode=WAL")]
    [InlineData("DESCRIBE orders")]
    public void UnmodeledUtilityNeverGetsAnIncompleteTrustedIntent(string sql)
    {
        var statement = Assert.Single(Analyze(sql).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.UtilityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.Unknown,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_UTILITY_UNSUPPORTED");
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Oracle, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Firebird, "EXPLAIN SELECT 1")]
    [InlineData(SqlAgentToolType.Sqlite, "EXPLAIN ANALYZE SELECT 1")]
    [InlineData(SqlAgentToolType.MySQL, "VACUUM app.orders")]
    [InlineData(SqlAgentToolType.MySQL, "ANALYZE app.orders")]
    [InlineData(SqlAgentToolType.MsSqlServer, "ANALYZE app.orders")]
    public void ProviderGateMustNotBorrowAnotherDatabaseUtilityGrammar(SqlAgentToolType provider, string sql)
    {
        var statement = Assert.Single(Analyze(sql, provider).Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Empty(statement.UtilityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Fact]
    public void ExplainDoesNotBypassSqlCoreQueryAndDmlParser()
    {
        var statement = Assert.Single(Analyze("EXPLAIN SELECT * FROM WHERE;").Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Empty(statement.UtilityIntents);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Fact]
    public void ExplainAnalyzeInsideTransactionMustNotPreserveAFalseRollbackProof()
    {
        var result = Analyze("BEGIN; EXPLAIN ANALYZE SELECT 1; ROLLBACK;");
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Single(result.UtilityIntents);
    }

    [Fact]
    public void PlanOnlyExplainInsideTransactionRemainsConservative()
    {
        var result = Analyze("BEGIN; EXPLAIN SELECT 1; ROLLBACK;");
        Assert.Single(result.UtilityIntents);
        Assert.Equal(SqlTransactionState.Unknown, result.FinalTransaction);
    }
}
