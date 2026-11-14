using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Runtime-effect certainty may never be promoted from an unproven
/// transaction. Typed operations may remain recognizable, but effects,
/// catalog deltas and bindings must not masquerade as known execution.
/// </summary>
public sealed class SqlCoreUnknownTransactionEffectCertaintyTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("COMMIT;", SqlTransactionControlOperation.Commit)]
    [InlineData("COMMIT WORK;", SqlTransactionControlOperation.Commit)]
    [InlineData("ROLLBACK;", SqlTransactionControlOperation.Rollback)]
    [InlineData("ROLLBACK WORK;", SqlTransactionControlOperation.Rollback)]
    public void OracleRecognizedTransactionControlInUnknownStateDoesNotClaimKnownEffect(
        string command, SqlTransactionControlOperation operation)
    {
        var report = Analyze(
            "CREATE TABLE orders(ID NUMBER); UNRECOGNIZED WORDS; " + command,
            SqlAgentToolType.Oracle);
        Assert.Equal(3, report.Statements.Count);
        var statement = report.Statements[2];

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal(SqlTransactionState.Unknown, statement.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, statement.TransactionAfter);
        Assert.Equal(SqlCatalogKnowledge.Unknown, statement.Certainty);
        Assert.Equal(SqlTransactionEffectOrigin.UnknownTransactionState,
            statement.TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven,
            statement.TransactionProvenance.RollbackScope);
        Assert.Equal(operation, statement.TransactionProvenance.ControlOperation);
        Assert.Equal(SqlEffectKind.ChangeTransaction, statement.Effects);
        var effect = Assert.Single(statement.DetailedEffects);
        Assert.Equal(SqlStatementEffectOperation.TransactionControl, effect.Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        Assert.Empty(statement.Changes);
        Assert.Empty(statement.Diagnostics);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void UnknownTransactionCannotMintKnownDdlOrDmlEvidenceAcrossSixDialects(
        SqlAgentToolType dialect, string typeName)
    {
        var report = Analyze(
            $"CREATE TABLE users(id {typeName}); " +
            "UNRECOGNIZED WORDS; " +
            $"CREATE TABLE orders(id {typeName}); " +
            "INSERT INTO users(id) VALUES (1); " +
            "SELECT id FROM users;", dialect);
        Assert.Equal(5, report.Statements.Count);
        var opaque = report.Statements[1];
        var ddl = report.Statements[2];
        var dml = report.Statements[3];
        var query = report.Statements[4];

        Assert.Equal(SqlTransactionState.Unknown, opaque.TransactionAfter);
        Assert.Equal(SqlTransactionState.Unknown, ddl.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, ddl.TransactionAfter);
        Assert.Equal(SqlCatalogKnowledge.Unknown, ddl.Certainty);
        Assert.NotEqual(SqlEffectCoverage.Complete, ddl.EffectCoverage);
        Assert.DoesNotContain(ddl.Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.DoesNotContain(ddl.DetailedEffects,
            effect => effect.Certainty == SqlCatalogKnowledge.Known);
        Assert.False(ddl.CatalogAfter.IsClosedWorld);
        Assert.True(ddl.Effects.HasFlag(SqlEffectKind.ChangeSchema));

        Assert.Equal(SqlTransactionState.Unknown, dml.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, query.TransactionBefore);
        Assert.Equal(SqlCatalogKnowledge.Unknown, dml.Certainty);
        Assert.Equal(SqlCatalogKnowledge.Unknown, query.Certainty);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, dml.CatalogBinding);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, query.CatalogBinding);
        Assert.DoesNotContain(dml.DetailedEffects,
            effect => effect.Certainty == SqlCatalogKnowledge.Known);
        Assert.DoesNotContain(query.DetailedEffects,
            effect => effect.Certainty == SqlCatalogKnowledge.Known);
        Assert.True(dml.Effects.HasFlag(SqlEffectKind.WriteData));
        Assert.True(query.Effects.HasFlag(SqlEffectKind.ReadData));
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }

    [Fact]
    public void InvalidDdlAfterUnknownTransactionCannotClaimKnownCertainty()
    {
        var report = Analyze(
            "UNRECOGNIZED WORDS; CREATE TABLE users(id INTEGER, id TEXT);");
        var ddl = report.Statements[1];

        Assert.Equal(SqlScriptStatementKind.Schema, ddl.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, ddl.Status);
        Assert.Equal(SqlTransactionState.Unknown, ddl.TransactionBefore);
        Assert.Equal(SqlCatalogKnowledge.Unknown, ddl.Certainty);
        Assert.Empty(ddl.Changes);
        Assert.DoesNotContain(ddl.DetailedEffects,
            effect => effect.Certainty == SqlCatalogKnowledge.Known);
        Assert.Equal(SqlEffectCoverage.Unknown, ddl.EffectCoverage);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("COMMIT;", SqlTransactionControlOperation.Commit)]
    [InlineData("ROLLBACK;", SqlTransactionControlOperation.Rollback)]
    public void OutsideOracleTclStillHasKnownStaticCatalogControlEvidence(
        string command, SqlTransactionControlOperation operation)
    {
        var statement = Assert.Single(Analyze(
            "CREATE TABLE users(id NUMBER); " + command,
            SqlAgentToolType.Oracle).Statements.Skip(1));

        Assert.Equal(SqlTransactionState.Outside, statement.TransactionBefore);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, statement.Certainty);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(statement.DetailedEffects).Certainty);
        Assert.Equal(operation, statement.TransactionProvenance.ControlOperation);
    }

    [Fact]
    public void ModeledRollbackRetainsKnownStaticCatalogDelta()
    {
        var report = Analyze(
            "BEGIN; CREATE TABLE users(id INTEGER); ROLLBACK;");
        var rollback = report.Statements[2];

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, rollback.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, rollback.Certainty);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(rollback.Changes).Certainty);
        Assert.Contains(rollback.DetailedEffects,
            effect => effect.Operation == SqlStatementEffectOperation.TransactionControl &&
                effect.Certainty == SqlCatalogKnowledge.Known);
        Assert.Equal(SqlTransactionState.Outside, rollback.TransactionAfter);
        Assert.True(report.FinalCatalog.IsClosedWorld);
    }
}
