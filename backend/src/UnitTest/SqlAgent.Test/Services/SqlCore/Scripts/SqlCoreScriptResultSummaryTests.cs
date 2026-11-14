using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Public analysis summaries must never promote unsupported SQL to proof.</summary>
public sealed class SqlCoreScriptResultSummaryTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Fact]
    public void ProvenDdlScript_AggregatesOrderedCatalogChangesAndCompleteEffects()
    {
        var result = Analyze("CREATE TABLE t (id INTEGER); ALTER TABLE t ADD COLUMN note TEXT; DROP TABLE t;");
        Assert.Equal(3, result.Statements.Count);
        Assert.All(result.Statements, item => Assert.Equal(SqlEffectCoverage.Complete, item.EffectCoverage));
        Assert.Equal(SqlEffectCoverage.Complete, result.EffectCoverage);

        var fromStatements = result.Statements.SelectMany(item => item.Changes).ToArray();
        Assert.NotEmpty(fromStatements);
        Assert.Equal(fromStatements, result.CatalogChanges);
        Assert.Equal(fromStatements.Select(x => x.Operation),
            result.CatalogChanges.Select(x => x.Operation));
        Assert.Equal(fromStatements.Select(x => x.Span.Start),
            result.CatalogChanges.Select(x => x.Span.Start));
    }

    [Fact]
    public void PartiallyModeledDcl_MakesScriptCoveragePartialNotComplete()
    {
        var result = Analyze("CREATE TABLE t (id INT); GRANT SELECT ON t TO reader;");
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[0].EffectCoverage);
        Assert.Equal(SqlEffectCoverage.Partial, result.Statements[1].EffectCoverage);
        Assert.Equal(SqlEffectCoverage.Partial, result.EffectCoverage);
        Assert.Equal(result.Statements.SelectMany(x => x.Changes), result.CatalogChanges);
    }

    [Fact]
    public void InvalidOrUnsupportedStatement_PreventsFullScriptCoverage()
    {
        var invalid = Analyze("CREATE TABLE t (id INTEGER); CREATE TABLE broken (id INT, id TEXT);");
        Assert.Equal(SqlEffectCoverage.Unknown, invalid.EffectCoverage);
        Assert.Equal(invalid.Statements.SelectMany(x => x.Changes), invalid.CatalogChanges);

        var unsupported = Analyze("CREATE TABLE t (id INTEGER); UNRECOGNIZED WORDS;");
        Assert.Equal(SqlEffectCoverage.Unknown, unsupported.EffectCoverage);
        Assert.Equal(unsupported.Statements.SelectMany(x => x.Changes), unsupported.CatalogChanges);
    }

    [Fact]
    public void EmptyScriptHasNoEffectsAndNoCatalogChanges()
    {
        var result = Analyze(" -- no statements\n");
        Assert.Empty(result.Statements);
        Assert.Empty(result.CatalogChanges);
        Assert.Equal(SqlEffectCoverage.Complete, result.EffectCoverage);
    }

    [Fact]
    public void AggregatedCatalogChanges_AreReadonlySnapshot()
    {
        var result = Analyze("CREATE TABLE t(id INTEGER);");
        var changes = Assert.IsAssignableFrom<IList<SqlCatalogChange>>(result.CatalogChanges);
        Assert.Single(changes);
        Assert.Throws<NotSupportedException>(() => changes[0] = changes[0]);
    }
}
