using System;
using System.Collections.Generic;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Strict, non-executable proof: mandatory equalities constrain a UNIQUE key
/// transitively from a fixed target row or literal. Nullable keys require
/// independently proven non-NULL semantics along a null-safe equality path.
/// </summary>
public sealed class SqlCoreMutationTransitiveEqualityClosureTests
{
    private const string Ddl =
        "CREATE TABLE app.dest(id INTEGER PRIMARY KEY, optional INTEGER, label TEXT);" +
        "CREATE TABLE app.one(code INTEGER NOT NULL UNIQUE, bridge INTEGER, note TEXT);" +
        "CREATE TABLE app.nullable(code INTEGER, bridge INTEGER, note TEXT, CONSTRAINT uq_nullable UNIQUE(code));" +
        "CREATE TABLE app.second(code INTEGER PRIMARY KEY, bridge INTEGER, note TEXT);" +
        "CREATE TABLE app.pairs(k1 INTEGER NOT NULL, k2 INTEGER, bridge INTEGER, " +
        "CONSTRAINT uq_pairs UNIQUE(k1,k2));" +
        "CREATE TABLE app.bag(code INTEGER, bridge INTEGER);";

    private static SqlCatalogState Catalog()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            Ddl, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.Diagnostics);
        Assert.Equal(6, result.FinalCatalog.Tables.Count);
        return result.FinalCatalog;
    }

    private static SqlStatementAnalysis One(string sql, SqlCatalogState? catalog = null) =>
        Assert.Single(SqlCoreFacade.AnalyzeScript(sql + ";", SqlAgentToolType.Postgres,
            catalog ?? Catalog()).Statements);

    public static IEnumerable<object[]> Proven()
    {
        string[] sql =
        [
            "UPDATE app.dest d SET label = 'x' FROM app.one s WHERE d.id = s.bridge AND s.bridge = s.code",
            "DELETE FROM app.dest d USING app.one s WHERE s.code = s.bridge AND s.bridge = d.id",
            "UPDATE app.dest d SET label = s.note FROM app.one s WHERE s.bridge = d.id AND s.code = s.bridge",
            "DELETE FROM app.dest d USING app.one s WHERE s.code = s.bridge AND 11 = s.bridge",
            "UPDATE app.dest d SET label = 'x' FROM app.one s WHERE s.code IS NOT DISTINCT FROM s.bridge AND s.bridge IS NOT DISTINCT FROM d.optional",
            "DELETE FROM app.dest d USING app.one s WHERE d.optional IS NOT DISTINCT FROM s.bridge AND s.bridge IS NOT DISTINCT FROM s.code",
            "UPDATE app.dest d SET label = 'x' FROM app.nullable n WHERE n.code IS NOT DISTINCT FROM n.bridge AND n.bridge = d.optional",
            "DELETE FROM app.dest d USING app.nullable n WHERE d.optional = n.bridge AND n.bridge IS NOT DISTINCT FROM n.code",
            "UPDATE app.dest d SET label = 'x' FROM app.nullable n WHERE n.code IS NOT DISTINCT FROM n.bridge AND n.bridge IS NOT DISTINCT FROM d.id",
            "UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE d.id = p.bridge AND p.bridge = p.k1 AND p.k2 = 22",
            "DELETE FROM app.dest d USING app.pairs p WHERE p.k2 = 22 AND p.k1 = p.bridge AND p.bridge = d.id",
            "UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE p.k1 = p.bridge AND p.bridge = d.id AND p.k2 IS NOT DISTINCT FROM d.id",
            "UPDATE app.dest d SET label = 'x' FROM app.one s, app.second t WHERE d.id = s.bridge AND s.bridge = s.code AND s.code = t.bridge AND t.bridge = t.code",
            "DELETE FROM app.dest d USING app.one s, app.second t WHERE s.code = s.bridge AND s.bridge = t.bridge AND d.id = t.bridge AND t.bridge = t.code",
            "UPDATE app.dest d SET label = 'x' FROM app.one s, app.second t WHERE s.code = t.bridge AND t.bridge = t.code AND t.code = d.id AND s.bridge = s.code",
            "DELETE FROM app.dest d USING app.one s WHERE d.id = s.bridge AND s.code = s.bridge AND s.note = 'active'"
        ];
        foreach (var statement in sql) yield return [statement];
    }

    [Theory]
    [MemberData(nameof(Proven))]
    public void MandatoryEquivalenceChainCanConstrainEveryPhysicalUniqueKey(string sql)
    {
        var catalog = Catalog();
        var stmt = One(sql, catalog);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne,
            stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
        Assert.Empty(stmt.Diagnostics);
        Assert.Same(catalog, stmt.CatalogAfter);
        Assert.Empty(stmt.Changes);
    }

    public static IEnumerable<object[]> Unproven()
    {
        string[] sql =
        [
            "UPDATE app.dest d SET label = 'x' FROM app.nullable n WHERE n.code IS NOT DISTINCT FROM n.bridge AND n.bridge IS NOT DISTINCT FROM d.optional",
            "DELETE FROM app.dest d USING app.nullable n WHERE d.optional IS NOT DISTINCT FROM n.bridge AND n.bridge IS NOT DISTINCT FROM n.code",
            "UPDATE app.dest d SET label = 'x' FROM app.nullable n WHERE n.bridge IS NOT DISTINCT FROM NULL AND n.code IS NOT DISTINCT FROM n.bridge",
            "DELETE FROM app.dest d USING app.nullable n WHERE n.code IS NOT DISTINCT FROM n.bridge AND n.bridge IS NOT DISTINCT FROM NULL",
            "UPDATE app.dest d SET label = 'x' FROM app.nullable n WHERE n.code IS NOT DISTINCT FROM n.bridge AND n.bridge IS NOT DISTINCT FROM n.code",
            "UPDATE app.dest d SET label = 'x' FROM app.one s WHERE d.id = s.bridge AND (s.bridge = s.code OR s.note = 'x')",
            "DELETE FROM app.dest d USING app.one s WHERE (d.id = s.bridge OR s.note = 'x') AND s.bridge = s.code",
            "UPDATE app.dest d SET label = 'x' FROM app.one s WHERE s.code = s.bridge AND s.bridge = s.bridge",
            "DELETE FROM app.dest d USING app.one s WHERE s.code = s.bridge",
            "UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE d.id = p.bridge AND p.bridge = p.k1",
            "DELETE FROM app.dest d USING app.pairs p WHERE p.k1 = p.bridge AND p.bridge = d.id AND p.k2 IS NOT DISTINCT FROM d.optional",
            "UPDATE app.dest d SET label = 'x' FROM app.one s, app.second t WHERE s.code = s.bridge AND s.bridge = t.bridge AND t.bridge = t.code",
            "DELETE FROM app.dest d USING app.one s, app.second t WHERE d.id = s.bridge AND s.bridge = s.code AND t.bridge = t.code",
            "UPDATE app.dest d SET label = 'x' FROM app.one s, app.bag b WHERE d.id = s.bridge AND s.bridge = s.code AND s.code = b.bridge AND b.bridge = b.code",
            "DELETE FROM app.dest d USING app.bag b WHERE d.id = b.bridge AND b.bridge = b.code",
            "UPDATE app.dest d SET label = 'x' FROM app.one s WHERE s.bridge = d.id AND s.code > s.bridge",
            "DELETE FROM app.dest d USING app.one s WHERE s.bridge = d.id AND NOT (s.code = s.bridge)"
        ];
        foreach (var statement in sql) yield return [statement];
    }

    [Theory]
    [MemberData(nameof(Unproven))]
    public void NullSafeNullableCyclesIncompleteKeysAndOrRemainUnverified(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
    }

    [Fact]
    public void UnknownCatalogCannotAcquireTransitiveEvidence()
    {
        var stmt = One("UPDATE app.dest d SET label = 'x' FROM app.one s WHERE d.id = s.bridge AND s.bridge = s.code",
            SqlCatalogState.Unknown());
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Fact]
    public void AViewNeverInheritsPhysicalUniqueKeyThroughAnEquivalenceChain()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            Ddl + "CREATE VIEW app.v AS SELECT code, bridge, note FROM app.one;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.Diagnostics);
        var stmt = One("UPDATE app.dest d SET label = 'x' FROM app.v v WHERE d.id = v.bridge AND v.bridge = v.code",
            result.FinalCatalog);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void SixProviderTablelessQueryRetainsNotApplicable(SqlAgentToolType dialect)
    {
        var stmt = Assert.Single(SqlCoreFacade.AnalyzeScript("SELECT 1;", dialect,
            SqlCatalogState.KnownEmpty()).Statements);
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable,
            stmt.SourceMatchCardinality);
    }
}
