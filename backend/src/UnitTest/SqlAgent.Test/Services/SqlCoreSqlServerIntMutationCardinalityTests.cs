using System;
using System.Collections.Generic;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server is not PostgreSQL: proven integer-key comparisons only,
/// after the explicit ASCII-safe catalog-identifier collation contract.
/// </summary>
public sealed class SqlCoreSqlServerIntMutationCardinalityTests
{
    private const string Ddl =
        "CREATE TABLE dbo.dest (id INT NOT NULL PRIMARY KEY, bucket INT, label INT);" +
        "CREATE TABLE dbo.lookup (id INT NOT NULL PRIMARY KEY, bridge INT, payload INT);" +
        "CREATE TABLE dbo.more (id INT NOT NULL PRIMARY KEY, bridge INT);" +
        "CREATE TABLE dbo.bag (id INT, bridge INT);" +
        "CREATE TABLE dbo.wide (id BIGINT NOT NULL PRIMARY KEY, bridge INT);" +
        "CREATE TABLE dbo.textkey (id VARCHAR(40) NOT NULL PRIMARY KEY, bridge INT);";

    private static SqlProviderCapabilityProfile Profile(string collation = "Latin1_General_100_CI_AS") =>
        new(SqlAgentToolType.MsSqlServer, null, default, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["CATALOG_COLLATION"] = collation });

    private static SqlCatalogState Catalog()
    {
        var analysis = SqlCoreFacade.AnalyzeScript(Ddl,
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty(), Profile());
        Assert.Empty(analysis.Diagnostics);
        Assert.True(analysis.FinalCatalog.IsClosedWorld);
        Assert.Equal(6, analysis.FinalCatalog.Tables.Count);
        return analysis.FinalCatalog;
    }

    private static SqlStatementAnalysis One(string sql,
        SqlProviderCapabilityProfile? profile = null,
        SqlCatalogState? catalog = null) =>
        Assert.Single(SqlCoreFacade.AnalyzeScript(sql + ";",
            SqlAgentToolType.MsSqlServer, catalog ?? Catalog(),
            profile ?? Profile()).Statements);

    public static IEnumerable<object[]> ProvenCases()
    {
        string[] statements =
        [
            "UPDATE dbo.dest SET label = s.payload FROM dbo.lookup s WHERE dbo.dest.id = s.id",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE s.id = dbo.dest.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE s.id = 17",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE 17 = s.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE dbo.dest.id = s.bridge AND s.bridge = s.id",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE s.id = s.bridge AND s.bridge = dbo.dest.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s, dbo.more m WHERE dbo.dest.id = s.id AND s.id = m.id",
            "DELETE FROM dbo.dest FROM dbo.lookup s, dbo.more m WHERE dbo.dest.id = s.id AND s.id = m.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s, dbo.more m WHERE dbo.dest.id = s.bridge AND s.bridge = s.id AND s.id = m.bridge AND m.bridge = m.id",
            "DELETE FROM dbo.dest FROM dbo.lookup s, dbo.more m WHERE m.id = m.bridge AND m.bridge = s.id AND s.id = s.bridge AND s.bridge = dbo.dest.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE s.id = dbo.dest.bucket",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE s.id = s.bridge AND s.bridge = 42"
        ];
        foreach (var statement in statements) yield return [statement];
    }

    [Theory]
    [MemberData(nameof(ProvenCases))]
    public void ExplicitCollationAndExactIntKeyCanProveJoinedMatchUpperBound(string sql)
    {
        var state = Catalog();
        var stmt = One(sql, catalog: state);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
        Assert.Empty(stmt.Diagnostics);
        Assert.Empty(stmt.Changes);
        Assert.Same(state, stmt.CatalogAfter);
    }

    public static IEnumerable<object[]> UnverifiedCases()
    {
        string[] statements =
        [
            "UPDATE dbo.dest SET label = 1 FROM dbo.bag b WHERE dbo.dest.id = b.id",
            "DELETE FROM dbo.dest FROM dbo.bag b WHERE dbo.dest.id = b.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE dbo.dest.id = s.bridge",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE s.bridge = dbo.dest.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE s.id = s.bridge",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE s.id = s.bridge",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE dbo.dest.id = s.bridge AND (s.bridge = s.id OR s.payload = 4)",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE dbo.dest.id = s.id OR s.id = 2",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s, dbo.bag b WHERE dbo.dest.id = s.id AND b.id = s.id",
            "DELETE FROM dbo.dest FROM dbo.lookup s, dbo.bag b WHERE dbo.dest.id = s.id AND b.id = s.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.wide w WHERE dbo.dest.id = w.id",
            "DELETE FROM dbo.dest FROM dbo.wide w WHERE dbo.dest.id = w.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.textkey t WHERE dbo.dest.id = t.id",
            "DELETE FROM dbo.dest FROM dbo.textkey t WHERE dbo.dest.id = t.id",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE s.id = 2147483648",
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE s.id = 1.5",
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE s.id = '1'"
        ];
        foreach (var statement in statements) yield return [statement];
    }

    [Theory]
    [MemberData(nameof(UnverifiedCases))]
    public void NoImplicitFloatTextWideOrUnanchoredCardinalityProof(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
    }

    [Theory]
    [InlineData("Latin1_General_100_CS_AS")]
    [InlineData("Turkish_100_CI_AS")]
    [InlineData("unknown_CI_AS")]
    public void UnmodeledIdentifierCollationKeepsBindingAndMatchUnverified(string value)
    {
        var stmt = One(
            "UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE dbo.dest.id = s.id",
            profile: Profile(value));
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.Contains(stmt.Diagnostics,
            d => d.Code == "SQL_CATALOG_BINDING_IDENTIFIER_RULES_UNPROVEN");
    }

    [Fact]
    public void MissingProfileNeverInheritsIdentifierProof()
    {
        var stmt = Assert.Single(SqlCoreFacade.AnalyzeScript(
            "DELETE FROM dbo.dest FROM dbo.lookup s WHERE dbo.dest.id = s.id;",
            SqlAgentToolType.MsSqlServer, Catalog()).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Fact]
    public void UnknownWorldNeverProvesMatches()
    {
        var stmt = One("UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE dbo.dest.id = s.id",
            catalog: SqlCatalogState.Unknown());
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Theory]
    [InlineData("UPDATE dbo.dest SET label = 1 WHERE id = 1")]
    [InlineData("DELETE FROM dbo.dest WHERE id = 1")]
    [InlineData("INSERT INTO dbo.dest(id,label) VALUES (1,1)")]
    [InlineData("SELECT id FROM dbo.dest")]
    public void NoJoinIsNotApplicable(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable, stmt.SourceMatchCardinality);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void SixProviderUnrelatedSelectDoesNotInheritJoinedMutationProof(SqlAgentToolType provider)
    {
        var stmt = Assert.Single(SqlCoreFacade.AnalyzeScript("SELECT 1;", provider,
            SqlCatalogState.KnownEmpty()).Statements);
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable, stmt.SourceMatchCardinality);
    }
}
