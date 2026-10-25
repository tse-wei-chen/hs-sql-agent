using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreStructuredStatementEffectsTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Fact]
    public void ProvenDdlProducesOneStructuredEffectPerActualCatalogDelta()
    {
        var sql = "CREATE TABLE public.\"Orders\" (id INTEGER); ALTER TABLE public.\"Orders\" ADD COLUMN note TEXT;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        var create = Assert.Single(result.Statements[0].DetailedEffects);
        Assert.Equal(SqlEffectKind.ChangeSchema, create.Kind);
        Assert.Equal(SqlStatementEffectOperation.CreateTable, create.Operation);
        Assert.Equal("public.Orders", create.ObjectName);
        Assert.Equal(new[] { "public", "Orders" }, create.ObjectIdentity.Parts);
        Assert.Equal(SqlCatalogKnowledge.Known, create.Certainty);
        Assert.Equal(result.Statements[0].Syntax.Span.Start, create.Span.Start);
        var add = Assert.Single(result.Statements[1].DetailedEffects);
        Assert.Equal(SqlStatementEffectOperation.AddColumn, add.Operation);
        Assert.Equal("public.Orders", add.ObjectName);
        Assert.Equal(SqlCatalogKnowledge.Known, add.Certainty);
        Assert.Equal(2, result.DetailedEffects.Count);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[0].EffectCoverage);
    }

    [Fact]
    public void InsertSelectReportsTypedWriteAndPhysicalReadDependency()
    {
        var result = Analyze(
            "CREATE TABLE source (id INTEGER); CREATE TABLE destination (id INTEGER);" +
            " INSERT INTO destination(id) SELECT id FROM source;");
        var statement = result.Statements[2];
        var write = Assert.Single(statement.DetailedEffects.Where(e => e.Kind == SqlEffectKind.WriteData));
        Assert.Equal(SqlStatementEffectOperation.Insert, write.Operation);
        Assert.Equal("destination", write.ObjectName);
        Assert.Contains("source", write.Dependencies);
        var read = Assert.Single(statement.DetailedEffects.Where(e => e.Kind == SqlEffectKind.ReadData));
        Assert.Equal(SqlStatementEffectOperation.ReadTable, read.Operation);
        Assert.Equal("source", read.ObjectName);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Null(read.ObjectIdentity);
    }

    [Fact]
    public void SelectSourcesUseAstInspectionNotKeywordMatching()
    {
        var result = Analyze("CREATE TABLE users (id INTEGER); SELECT id FROM users;");
        var statement = result.Statements[1];
        var effect = Assert.Single(statement.DetailedEffects);
        Assert.Equal(SqlStatementEffectOperation.ReadTable, effect.Operation);
        Assert.Equal("users", effect.ObjectName);
        Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
    }

    [Fact]
    public void KnownNoOpDdlDoesNotInventCatalogChanges()
    {
        var result = Analyze(
            "CREATE TABLE t(id INTEGER); CREATE TABLE IF NOT EXISTS t(other INTEGER);");
        Assert.Empty(result.Statements[1].Changes);
        Assert.Empty(result.Statements[1].DetailedEffects);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[1].EffectCoverage);
        Assert.Single(result.DetailedEffects);
    }

    [Fact]
    public void UnsupportedStatementsPreserveUnknownPotentialEffects()
    {
        var result = Analyze("GRANT SELECT ON users TO reader;");
        var statement = Assert.Single(result.Statements);
        var effect = Assert.Single(statement.DetailedEffects);
        Assert.Equal(SqlStatementEffectOperation.Unknown, effect.Operation);
        Assert.True(effect.Kind.HasFlag(SqlEffectKind.ChangePermissions));
        Assert.True(effect.Kind.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
    }

    [Fact]
    public void InvalidStatementNeverAcquiresAProvenEffect()
    {
        var result = Analyze("CREATE TABLE bad (id INTEGER, id TEXT);");
        var statement = Assert.Single(result.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.DoesNotContain(statement.DetailedEffects, e => e.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void CatalogEffectIdentityDoesNotInventNamesForUnknownChanges()
    {
        var result = Analyze("CREATE TABLE t(id INTEGER DEFAULT 1);");
        var effect = Assert.Single(Assert.Single(result.Statements).DetailedEffects);
        Assert.Equal(SqlStatementEffectOperation.Unknown, effect.Operation);
        Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        Assert.Null(effect.ObjectIdentity);
    }
}
