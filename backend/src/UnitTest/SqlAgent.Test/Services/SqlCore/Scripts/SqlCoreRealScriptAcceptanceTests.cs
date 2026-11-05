using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// P6 real-world multi-statement SQL acceptance corpus: independently named
/// scripts for all six source dialects, with strict semantic checkpoints.
/// Unlike grammar-only tests, every case must pass through AnalyzeScript.
/// No DB connection or SQL execution is involved.
/// </summary>
public sealed class SqlCoreRealScriptAcceptanceTests
{
    private sealed class ScriptCase
    {
        public string Id { get; set; } = "";
        public string Dialect { get; set; } = "";
        public string Group { get; set; } = "";
        public string Sql { get; set; } = "";
        public int StatementCount { get; set; }
        public int CatalogChanges { get; set; }
        public int FailureIndex { get; set; } = -1;
    }

    private const string ResourceName =
        "SqlAgent.Test.ScriptCorpus.p6-real-world-script-acceptance.json";

    private static readonly Lazy<IReadOnlyList<ScriptCase>> Corpus = new(() =>
    {
        using var stream = typeof(SqlCoreRealScriptAcceptanceTests).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Missing embedded Script acceptance corpus: {ResourceName}");
        var entries = JsonSerializer.Deserialize<ScriptCase[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Script acceptance corpus is empty");
        return entries;
    });

    public static IEnumerable<object[]> Cases =>
        Corpus.Value.Select(x => new object[] { x.Id });

    [Fact]
    public void AcceptanceCorpus_ContainsAtLeast240IndividuallyNamedMultiStatementScripts()
    {
        var cases = Corpus.Value;
        Assert.True(cases.Count >= 240, $"Found only {cases.Count} Script examples");
        Assert.Equal(cases.Count, cases.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(cases.Count, cases.Select(x => x.Sql).Distinct(StringComparer.Ordinal).Count());

        var providers = Enum.GetNames<SqlAgentToolType>();
        Assert.Equal(6, providers.Length);
        foreach (var provider in providers)
        {
            var subset = cases.Where(c => c.Dialect == provider.ToString()).ToArray();
            Assert.True(subset.Length >= 40,
                $"Missing Script examples for {provider}: {subset.Length}");
            foreach (var group in new[] { "Catalog", "Mixed", "Invalid", "Unsupported" })
                Assert.True(subset.Count(x => x.Group == group) >= 8,
                    $"Missing {provider}/{group} Script cases");
        }
        Assert.All(cases, entry =>
        {
            Assert.True(entry.StatementCount >= 2, entry.Id);
            Assert.Contains(';', entry.Sql);
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.Sql));
        });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RealScript_ParsesAndAnalyzesWithDeclaredSemanticProof(string id)
    {
        var example = Corpus.Value.Single(x => x.Id == id);
        var dialect = Enum.Parse<SqlAgentToolType>(example.Dialect);
        var parsed = SqlCoreFacade.ParseScript(example.Sql,
            new SqlScriptParseOptions(dialect));
        var analysis = SqlCoreFacade.AnalyzeScript(example.Sql,
            new SqlScriptAnalysisOptions(dialect, SqlCatalogState.KnownEmpty()));

        Assert.Equal(example.StatementCount, parsed.Statements.Count);
        Assert.Equal(parsed.Statements.Count, analysis.Statements.Count);
        Assert.True(parsed.BatchCount >= 1, id);

        int priorEnd = -1;
        for (var index = 0; index < parsed.Statements.Count; index++)
        {
            var syntax = parsed.Statements[index];
            Assert.Equal(syntax.Kind, analysis.Statements[index].Syntax.Kind);
            Assert.Equal(syntax.Span.Start,
                analysis.Statements[index].Syntax.Span.Start);
            Assert.Equal(syntax.Text,
                example.Sql.Substring(syntax.Span.Start, syntax.Span.Length));
            Assert.True(syntax.Span.Length > 0, id);
            Assert.True(syntax.Span.Start >= priorEnd,
                $"{id}: overlapping/nonmonotonic source spans at {index}");
            priorEnd = syntax.Span.Start + syntax.Span.Length;
            Assert.True(syntax.BatchIndex >= 0);
        }

        Assert.Equal(analysis.Statements.Sum(x => x.Changes.Count),
            analysis.CatalogChanges.Count);
        Assert.Equal(analysis.Statements.SelectMany(x => x.Changes).Select(x => x.Operation),
            analysis.CatalogChanges.Select(x => x.Operation));
        Assert.All(analysis.Dependencies, dependency =>
        {
            Assert.InRange(dependency.StatementIndex, 0, example.StatementCount - 1);
            Assert.Equal(SqlCatalogKnowledge.Unknown, dependency.Certainty);
        });

        switch (example.Group)
        {
            case "Catalog":
                Assert.All(analysis.Statements, x =>
                {
                    Assert.Equal(SqlScriptStatementKind.Schema, x.Syntax.Kind);
                    Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status);
                    Assert.Equal(SqlCatalogKnowledge.Known, x.Certainty);
                });
                Assert.Empty(analysis.Diagnostics);
                Assert.Equal(SqlEffectCoverage.Complete, analysis.EffectCoverage);
                Assert.Equal(example.CatalogChanges, analysis.CatalogChanges.Count);
                Assert.True(analysis.FinalCatalog.IsClosedWorld);
                break;
            case "Mixed":
                Assert.Equal(SqlScriptStatementKind.Schema, analysis.Statements[0].Syntax.Kind);
                Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[0].Status);
                Assert.All(analysis.Statements.Skip(1), x =>
                {
                    Assert.True(x.Syntax.Kind == SqlScriptStatementKind.Query
                        || x.Syntax.Kind == SqlScriptStatementKind.Mutation,
                        $"{id}: unexpected statement kind {x.Syntax.Kind}");
                    Assert.Equal(SqlScriptParseStatus.Parsed, x.Syntax.Status);
                    Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, x.Status);
                });
                Assert.Equal(example.CatalogChanges, analysis.CatalogChanges.Count);
                Assert.True(analysis.FinalCatalog.IsClosedWorld);
                break;
            case "Invalid":
                Assert.Equal(SqlStatementAnalysisStatus.Invalid,
                    analysis.Statements[example.FailureIndex].Status);
                Assert.Empty(analysis.Statements[example.FailureIndex].Changes);
                Assert.Equal(SqlEffectCoverage.Unknown, analysis.EffectCoverage);
                Assert.NotEmpty(analysis.Diagnostics);
                break;
            case "Unsupported":
                Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
                    analysis.Statements[example.FailureIndex].Status);
                Assert.Equal(SqlEffectCoverage.Unknown, analysis.EffectCoverage);
                Assert.False(analysis.FinalCatalog.IsClosedWorld);
                Assert.NotEmpty(analysis.Diagnostics);
                break;
            default:
                throw new InvalidOperationException($"{id}: unknown scenario group");
        }
    }
}
