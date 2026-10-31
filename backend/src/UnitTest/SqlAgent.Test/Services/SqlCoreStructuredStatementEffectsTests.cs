using System;
using System.Diagnostics;
using System.Collections.Generic;
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
    public void ParsedDclPreservesUnknownEffectivePermissionCertainty()
    {
        var result = Analyze("GRANT SELECT ON users TO reader;");
        var statement = Assert.Single(result.Statements);
        var effect = Assert.Single(statement.DetailedEffects);
        Assert.Equal(SqlStatementEffectOperation.GrantPrivilege, effect.Operation);
        Assert.Equal(SqlEffectKind.ChangePermissions, effect.Kind);
        Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Equal(SqlCatalogKnowledge.Unknown, Assert.Single(statement.SecurityIntents).Certainty);
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

    private sealed class Listener<T>(Action<T> action) : IObserver<T>
    {
        public void OnNext(T value) => action(value);
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ScriptAnalysis_UsesParsedStatementWithoutRedundantCatalogParse(SqlAgentToolType dialect)
    {
        // The existing Source parser runs once for each visible Query/Mutation;
        // strict Catalog proof and Effects must reuse its typed statement.
        var owner = Environment.CurrentManagedThreadId;
        var stages = new List<string>();
        var listeners = new List<IDisposable>();
        using (DiagnosticListener.AllListeners.Subscribe(new Listener<DiagnosticListener>(source =>
        {
            if (source.Name == "HsSqlAgent.SqlCore.Compiler")
                listeners.Add(source.Subscribe(new Listener<KeyValuePair<string, object?>>(entry =>
                {
                    if (entry.Key == "StageCompleted" && Environment.CurrentManagedThreadId == owner
                        && entry.Value is not null)
                    {
                        var stage = entry.Value.GetType().GetProperty("Stage")?.GetValue(entry.Value) as string;
                        if (stage is not null) stages.Add(stage);
                    }
                })));
        })))
        {
            try
            {
                var result = SqlCoreFacade.AnalyzeScript(
                    "SELECT 1 AS first_value; SELECT 2 AS second_value;",
                    dialect, SqlCatalogState.KnownEmpty());
                Assert.Equal(2, result.Statements.Count);
                Assert.All(result.Statements, statement =>
                {
                    Assert.Equal(SqlScriptParseStatus.Parsed, statement.Syntax.Status);
                    Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
                    Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
                    Assert.Single(statement.DetailedEffects);
                });
                Assert.Equal(2, stages.Count(stage => stage == "ScriptStatementParse"));
                Assert.DoesNotContain("CatalogParsing", stages);
            }
            finally
            {
                foreach (var disposable in listeners) disposable.Dispose();
            }
        }
    }
}
