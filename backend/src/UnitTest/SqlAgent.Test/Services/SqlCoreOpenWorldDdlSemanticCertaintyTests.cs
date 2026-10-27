using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A proven local DDL error is a rejection, not a proof that an open-world
/// catalog is complete. The same invalid grammar is checked under both
/// known-empty and unknown caller-supplied catalog premises.
/// </summary>
public sealed class SqlCoreOpenWorldDdlSemanticCertaintyTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void InvalidDdlInUnknownCatalogMustNotPromoteCatalogCertainty(
        SqlAgentToolType provider, string typeName)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users(id {typeName}, id {typeName});",
            provider, SqlCatalogState.Unknown());
        var ddl = Assert.Single(result.Statements);

        Assert.Equal(SqlScriptStatementKind.Schema, ddl.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, ddl.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, ddl.Certainty);
        Assert.Equal(SqlTransactionState.Outside, ddl.TransactionAfter);
        Assert.False(ddl.CatalogBefore.IsClosedWorld);
        Assert.False(ddl.CatalogAfter.IsClosedWorld);
        Assert.Empty(ddl.Changes);
        Assert.Empty(ddl.DetailedEffects);
        Assert.Equal(SqlEffectCoverage.Unknown, ddl.EffectCoverage);
        Assert.True(ddl.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Contains(ddl.Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void KnownEmptyCatalogStillSupportsCertainSemanticRejection(
        SqlAgentToolType provider, string typeName)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users(id {typeName}, id {typeName});",
            provider, SqlCatalogState.KnownEmpty());
        var ddl = Assert.Single(result.Statements);

        Assert.Equal(SqlStatementAnalysisStatus.Invalid, ddl.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, ddl.Certainty);
        Assert.Equal(SqlTransactionState.Outside, ddl.TransactionAfter);
        Assert.True(ddl.CatalogBefore.IsClosedWorld);
        Assert.True(ddl.CatalogAfter.IsClosedWorld);
        Assert.Empty(ddl.Changes);
        Assert.Contains(ddl.Diagnostics,
            x => x.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void PartialCatalogCannotReestablishKnownThroughInvalidDdl()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE TABLE orders(id INTEGER, id INTEGER);" +
            "SELECT id FROM users;",
            SqlAgentToolType.Postgres, SqlCatalogState.Unknown());
        Assert.Equal(3, result.Statements.Count);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed,
            result.Statements[0].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            result.Statements[0].Certainty);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid,
            result.Statements[1].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            result.Statements[1].Certainty);
        Assert.False(result.Statements[1].CatalogAfter.IsClosedWorld);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified,
            result.Statements[2].CatalogBinding);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnsupportedDdlInvalidatesKnownObjectsAndLaterInvalidDdlStaysUnknown()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE TABLE unsupported(id INTEGER DEFAULT 1);" +
            "CREATE TABLE orders(id INTEGER, id INTEGER);" +
            "SELECT id FROM users;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(4, result.Statements.Count);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[2].Certainty);
        Assert.Empty(result.Statements[2].Changes);
        Assert.False(result.Statements[2].CatalogAfter.IsClosedWorld);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified,
            result.Statements[3].CatalogBinding);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void FullyModeledKnownDdlStillProducesExactCatalogChanges()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users(id INTEGER);" +
            "ALTER TABLE users ADD COLUMN note TEXT;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(2, result.Statements.Count);
        Assert.All(result.Statements, s =>
        {
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status);
            Assert.Equal(SqlCatalogKnowledge.Known, s.Certainty);
            Assert.Equal(SqlEffectCoverage.Complete, s.EffectCoverage);
            Assert.Equal(SqlCatalogKnowledge.Known,
                Assert.Single(s.Changes).Certainty);
            Assert.Equal(SqlCatalogKnowledge.Known,
                Assert.Single(s.DetailedEffects).Certainty);
        });
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }
}
