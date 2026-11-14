using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Outside is the static script coordinator's modeled starting point, not a
/// runtime connection-state attestation. An opaque statement can destroy that
/// proof even without a preceding modeled BEGIN.
/// </summary>
public sealed class SqlCoreOutsideOpaqueTransactionFenceTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnsupportedSessionCannotRetainOutsideTransactionProof(SqlAgentToolType dialect)
    {
        var report = Analyze("SET ROLE admin;", dialect);
        var statement = Assert.Single(report.Statements);

        Assert.Equal(SqlScriptStatementKind.Session, statement.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlTransactionState.Outside, statement.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, statement.TransactionAfter);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
            statement.TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven,
            statement.TransactionProvenance.RollbackScope);
        Assert.DoesNotContain(statement.Diagnostics,
            d => d.Code == "SQL_TRANSACTION_CONTEXT_UNPROVEN");
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DISCARD ALL;", SqlScriptStatementKind.Unknown,
        SqlStatementAnalysisStatus.Unsupported)]
    [InlineData("GRANT ALL ON app.users TO reader;", SqlScriptStatementKind.Security,
        SqlStatementAnalysisStatus.Unsupported)]
    [InlineData("CREATE TABLE bad(id INTEGER DEFAULT 1);", SqlScriptStatementKind.Schema,
        SqlStatementAnalysisStatus.Unsupported)]
    [InlineData("CALL app.reset();", SqlScriptStatementKind.Procedural,
        SqlStatementAnalysisStatus.PartiallyAnalyzed)]
    [InlineData("EXPLAIN ANALYZE SELECT 1;", SqlScriptStatementKind.Utility,
        SqlStatementAnalysisStatus.PartiallyAnalyzed)]
    public void OpaqueCrossFamilyStatementsCannotKeepOutside(
        string sql, SqlScriptStatementKind kind, SqlStatementAnalysisStatus status)
    {
        var statement = Assert.Single(Analyze(sql).Statements);

        Assert.Equal(kind, statement.Kind);
        Assert.Equal(status, statement.Status);
        Assert.Equal(SqlTransactionState.Outside, statement.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, statement.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
            statement.TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven,
            statement.TransactionProvenance.RollbackScope);
        Assert.DoesNotContain(statement.Diagnostics,
            d => d.Code == "SQL_TRANSACTION_CONTEXT_UNPROVEN");
        Assert.False(statement.CatalogAfter.IsClosedWorld);
    }

    [Fact]
    public void UnknownOutsideStateCannotBeReestablishedBySubsequentBeginRollback()
    {
        var report = Analyze("DISCARD ALL; BEGIN; ROLLBACK; SELECT 1;");

        Assert.Equal(4, report.Statements.Count);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[0].TransactionAfter);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[1].TransactionAfter);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[2].TransactionAfter);
        Assert.Equal(SqlTransactionState.Unknown, report.Statements[3].TransactionBefore);
        Assert.Equal(SqlTransactionEffectOrigin.UnknownTransactionState,
            report.Statements[3].TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT 1;")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT 1;")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT 1;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT 1;")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT 1;")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT 1;")]
    public void ModeledReadOnlySelectKeepsStaticOutside(SqlAgentToolType dialect, string sql)
    {
        var statement = Assert.Single(Analyze(sql, dialect).Statements);

        Assert.NotEqual(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlTransactionState.Outside, statement.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.OutsideTransaction,
            statement.TransactionProvenance.Origin);
    }

    [Theory]
    [InlineData("SET search_path TO app;")]
    [InlineData("CREATE TABLE users(id INTEGER);")]
    [InlineData("EXPLAIN SELECT 1;")]
    public void ProvenOrPlanningOnlyStatementsKeepOutside(string sql)
    {
        var report = Analyze(sql);
        var statement = Assert.Single(report.Statements);

        Assert.NotEqual(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlTransactionState.Outside, statement.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.OutsideTransaction,
            statement.TransactionProvenance.Origin);
        Assert.True(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AcceptedSqlFunctionDefinitionKeepsItsDiagnosticContract()
    {
        var statement = Assert.Single(Analyze(
            "CREATE FUNCTION app.positional(amount integer) RETURNS integer " +
            "LANGUAGE SQL AS $body$SELECT $1;$body$;").Statements);

        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlScriptStatementKind.Procedural, statement.Kind);
        Assert.Equal(SqlTransactionState.Unknown, statement.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
            statement.TransactionProvenance.Origin);
        Assert.Empty(statement.Diagnostics);
    }

    [Fact]
    public void SqlServerKnownNoCountDoesNotInvalidateOutside()
    {
        var statement = Assert.Single(Analyze("SET NOCOUNT ON;",
            SqlAgentToolType.MsSqlServer).Statements);

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal(SqlTransactionState.Outside, statement.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.OutsideTransaction,
            statement.TransactionProvenance.Origin);
    }

    [Fact]
    public void InvalidQuerySyntaxIsNotProofThatSqlExecuted()
    {
        var statement = Assert.Single(Analyze("SELECT FROM users;").Statements);

        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Equal(SqlTransactionState.Outside, statement.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.OutsideTransaction,
            statement.TransactionProvenance.Origin);
    }

    [Fact]
    public void OracleCommitRemainsRecognizedButCannotRecoverUnknownProof()
    {
        var report = Analyze(
            "CREATE TABLE orders(ID NUMBER); UNRECOGNIZED WORDS; COMMIT;",
            SqlAgentToolType.Oracle);
        var commit = report.Statements[2];

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, commit.Status);
        Assert.Equal(SqlTransactionControlOperation.Commit,
            commit.TransactionProvenance.ControlOperation);
        Assert.Equal(SqlTransactionState.Unknown, commit.TransactionBefore);
        Assert.Equal(SqlTransactionState.Unknown, commit.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.UnknownTransactionState,
            commit.TransactionProvenance.Origin);
        Assert.Equal(SqlTransactionRollbackScope.Unproven,
            commit.TransactionProvenance.RollbackScope);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
    }

    [Fact]
    public void TransactionControlOutsideAlreadyFailsClosedWithoutDuplicateFence()
    {
        var statement = Assert.Single(Analyze("ROLLBACK;").Statements);

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlTransactionState.Unknown, statement.TransactionAfter);
        Assert.Equal(SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
            statement.TransactionProvenance.Origin);
        Assert.DoesNotContain(statement.Diagnostics,
            d => d.Code == "SQL_TRANSACTION_CONTEXT_UNPROVEN");
        Assert.Contains(statement.Diagnostics,
            d => d.Code == "SQL_TRANSACTION_WITHOUT_BEGIN");
    }
}
