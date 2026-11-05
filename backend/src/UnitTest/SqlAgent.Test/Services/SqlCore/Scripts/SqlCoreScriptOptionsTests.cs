using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Script options are a CLR-friendly projection of the existing validated
/// source-profile/catalog/cancellation entry points; they add no execution proof.
/// </summary>
public sealed class SqlCoreScriptOptionsTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ParseOptions_AreEquivalentToLegacyScriptParsing(SqlAgentToolType provider)
    {
        const string sql = "SELECT 1; SELECT 2;";
        var options = new SqlScriptParseOptions(provider);
        var legacy = SqlCoreFacade.ParseScript(sql, provider);
        var actual = SqlCoreFacade.ParseScript(sql, options);

        Assert.Equal(provider, options.SourceDialect);
        Assert.Null(options.SourceProfile);
        Assert.Equal(CancellationToken.None, options.CancellationToken);
        Assert.Equal(legacy.BatchCount, actual.BatchCount);
        Assert.Equal(legacy.Diagnostics.Select(x => x.Code), actual.Diagnostics.Select(x => x.Code));
        Assert.Equal(
            legacy.Statements.Select(x => (x.Kind, x.Status, x.BatchIndex, x.Span.Start, x.Span.Length)),
            actual.Statements.Select(x => (x.Kind, x.Status, x.BatchIndex, x.Span.Start, x.Span.Length)));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void AnalyzeOptions_DefaultToUnknownCatalogAndMatchLegacy(SqlAgentToolType provider)
    {
        const string sql = "SELECT 1";
        var options = new SqlScriptAnalysisOptions(provider);
        var legacy = SqlCoreFacade.AnalyzeScript(sql, provider);
        var actual = SqlCoreFacade.AnalyzeScript(sql, options);

        Assert.Equal(provider, options.SourceDialect);
        Assert.Null(options.SourceProfile);
        Assert.Equal(CancellationToken.None, options.CancellationToken);
        Assert.NotNull(options.InitialCatalog);
        Assert.False(options.InitialCatalog.IsClosedWorld);
        Assert.Equal(legacy.Statements.Select(x => x.Status), actual.Statements.Select(x => x.Status));
        Assert.Equal(legacy.Statements.Select(x => x.EffectCoverage), actual.Statements.Select(x => x.EffectCoverage));
        Assert.Equal(legacy.EffectCoverage, actual.EffectCoverage);
        Assert.Equal(legacy.Diagnostics.Select(x => x.Code), actual.Diagnostics.Select(x => x.Code));
        Assert.Equal(legacy.FinalCatalog.IsClosedWorld, actual.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AnalysisOptions_KeepCallerCatalogAndPreserveProvenBinding()
    {
        const string sql = "CREATE TABLE orders(id INTEGER); SELECT id FROM orders;";
        var catalog = SqlCatalogState.KnownEmpty();
        var options = new SqlScriptAnalysisOptions(SqlAgentToolType.Postgres, catalog);
        var result = SqlCoreFacade.AnalyzeScript(sql, options);
        var legacy = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, catalog);

        Assert.Same(catalog, options.InitialCatalog);
        Assert.Equal(legacy.Statements.Select(x => x.Status), result.Statements.Select(x => x.Status));
        Assert.Equal(legacy.Statements.Select(x => x.CatalogBinding), result.Statements.Select(x => x.CatalogBinding));
        Assert.Equal(legacy.CatalogChanges.Select(x => (x.TableName, x.Operation)),
            result.CatalogChanges.Select(x => (x.TableName, x.Operation)));
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SourceProfileInOptions_UsesExactSameVersionGatedGrammar()
    {
        var profile = Oracle(21);
        const string sql = "SELECT 1 EXCEPT SELECT 2";
        var options = new SqlScriptParseOptions(SqlAgentToolType.Oracle, profile);
        var result = SqlCoreFacade.ParseScript(sql, options);
        var legacy = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Oracle, profile);

        Assert.Same(profile, options.SourceProfile);
        Assert.Equal(legacy.Statements.Select(x => x.Status), result.Statements.Select(x => x.Status));
        Assert.Equal(SqlScriptParseStatus.Parsed, Assert.Single(result.Statements).Status);

        var analysisOptions = new SqlScriptAnalysisOptions(
            SqlAgentToolType.Oracle, SqlCatalogState.Unknown(), profile);
        var analyzed = SqlCoreFacade.AnalyzeScript(sql, analysisOptions);
        var oldAnalysis = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Oracle, analysisOptions.InitialCatalog, profile);
        Assert.Equal(oldAnalysis.Statements.Select(x => x.Status),
            analyzed.Statements.Select(x => x.Status));
    }

    [Fact]
    public void Options_DoNotBorrowSourceCapabilityFromDifferentDialect()
    {
        var oracleProfile = Oracle(21);
        Assert.Throws<ArgumentException>(() => SqlCoreFacade.ParseScript(
            "SELECT 1", new SqlScriptParseOptions(SqlAgentToolType.Postgres, oracleProfile)));
        Assert.Throws<ArgumentException>(() => SqlCoreFacade.AnalyzeScript(
            "SELECT 1", new SqlScriptAnalysisOptions(
                SqlAgentToolType.Postgres, SqlCatalogState.Unknown(), oracleProfile)));
    }

    [Fact]
    public void CancellationInOptions_PropagatesToBothEntriesAndDoesNotPoisonLaterRequests()
    {
        using var cts = new CancellationTokenSource();
        var parse = new SqlScriptParseOptions(SqlAgentToolType.Postgres, cts.Token);
        var analyze = new SqlScriptAnalysisOptions(
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(), cts.Token);
        Assert.Equal(cts.Token, parse.CancellationToken);
        Assert.Equal(cts.Token, analyze.CancellationToken);
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlCoreFacade.ParseScript("SELECT 1", parse));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlCoreFacade.AnalyzeScript("SELECT 1", analyze));

        Assert.Single(SqlCoreFacade.ParseScript("SELECT 1",
            new SqlScriptParseOptions(SqlAgentToolType.Postgres)).Statements);
    }

    [Fact]
    public void FullOptionsConstructorsCombineSourceCatalogProfileAndCancellation()
    {
        var profile = Oracle(21);
        using var cts = new CancellationTokenSource();
        var catalog = SqlCatalogState.KnownEmpty();
        var parse = new SqlScriptParseOptions(SqlAgentToolType.Oracle, profile, cts.Token);
        var analyze = new SqlScriptAnalysisOptions(SqlAgentToolType.Oracle, catalog, profile, cts.Token);
        Assert.Same(profile, parse.SourceProfile);
        Assert.Same(profile, analyze.SourceProfile);
        Assert.Same(catalog, analyze.InitialCatalog);
        Assert.Equal(cts.Token, parse.CancellationToken);
        Assert.Equal(cts.Token, analyze.CancellationToken);

        Assert.Equal(SqlScriptParseStatus.Parsed,
            Assert.Single(SqlCoreFacade.ParseScript("SELECT 1 EXCEPT SELECT 2", parse).Statements).Status);
        Assert.Single(SqlCoreFacade.AnalyzeScript("SELECT 1", analyze).Statements);
    }

    [Fact]
    public void Options_NullInputsAreRejectedWithoutFallingBackToLegacyDefaults()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SqlCoreFacade.ParseScript("SELECT 1", (SqlScriptParseOptions)null!));
        Assert.Throws<ArgumentNullException>(() =>
            SqlCoreFacade.AnalyzeScript("SELECT 1", (SqlScriptAnalysisOptions)null!));
        Assert.Throws<ArgumentNullException>(() =>
            new SqlScriptAnalysisOptions(SqlAgentToolType.Postgres, null!));
    }

    [Theory]
    [InlineData(typeof(SqlScriptParseOptions))]
    [InlineData(typeof(SqlScriptAnalysisOptions))]
    public void PublicOptionsContainOnlyClrPropertiesAndNoMutableSetters(Type optionType)
    {
        Assert.True(optionType.IsSealed);
        var properties = optionType.GetProperties();
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            Assert.Null(property.SetMethod);
            Assert.DoesNotContain("Microsoft.FSharp", property.PropertyType.FullName ?? "",
                StringComparison.Ordinal);
        }
    }

    private static SqlProviderCapabilityProfile Oracle(int major) =>
        new(SqlAgentToolType.Oracle, new Version(major, 0),
            new HashSet<string>(), new Dictionary<string, string>());
}
