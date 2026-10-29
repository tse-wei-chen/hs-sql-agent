using System;
using System.Collections.Generic;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Closed-AST cardinality evidence must respect PostgreSQL NULL semantics:
/// ordinary '=' never matches NULL, but 'IS NOT DISTINCT FROM' does.
/// For a nullable UNIQUE key, multiple NULLs remain legal.
/// </summary>
public sealed class SqlCoreMutationNullSafeMatchProofTests
{
    private const string Ddl =
        "CREATE TABLE app.dest(id INTEGER PRIMARY KEY, tag INTEGER, label TEXT, note TEXT);" +
        "CREATE TABLE app.flex(code INTEGER, note TEXT, CONSTRAINT uq_flex UNIQUE(code));" +
        "CREATE TABLE app.required(code INTEGER NOT NULL, note TEXT, CONSTRAINT uq_required UNIQUE(code));" +
        "CREATE TABLE app.pk(code INTEGER PRIMARY KEY, note TEXT);" +
        "CREATE TABLE app.pairs(k1 INTEGER NOT NULL, k2 INTEGER, note TEXT, CONSTRAINT uq_pairs UNIQUE(k1,k2));" +
        "CREATE TABLE app.bag(code INTEGER, note TEXT);";

    private static SqlCatalogState Catalog()
    {
        var ddl = SqlCoreFacade.AnalyzeScript(Ddl,
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(ddl.Diagnostics);
        Assert.Equal(6, ddl.FinalCatalog.Tables.Count);
        return ddl.FinalCatalog;
    }

    private static SqlStatementAnalysis One(string sql, SqlCatalogState? catalog = null) =>
        Assert.Single(SqlCoreFacade.AnalyzeScript(
            sql + ";", SqlAgentToolType.Postgres, catalog ?? Catalog()).Statements);

    public static IEnumerable<object[]> Positive()
    {
        string[] statements =
        [
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE d.id = code",
            "DELETE FROM app.dest d USING app.flex f WHERE code = d.id",
            "UPDATE app.dest d SET label = 'x' FROM app.required r WHERE d.id IS NOT DISTINCT FROM r.code",
            "DELETE FROM app.dest d USING app.required r WHERE r.code IS NOT DISTINCT FROM d.tag",
            "UPDATE app.dest d SET label = 'x' FROM app.pk k WHERE d.tag IS NOT DISTINCT FROM k.code",
            "DELETE FROM app.dest d USING app.pk k WHERE k.code IS NOT DISTINCT FROM d.tag",
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE f.code IS NOT DISTINCT FROM d.id",
            "DELETE FROM app.dest d USING app.flex f WHERE d.id IS NOT DISTINCT FROM f.code",
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE f.code IS NOT DISTINCT FROM 17",
            "DELETE FROM app.dest d USING app.flex f WHERE 17 IS NOT DISTINCT FROM f.code",
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE f.code = d.tag",
            "UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE p.k1 = d.id AND p.k2 = d.tag",
            "UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE p.k1 = d.id AND p.k2 IS NOT DISTINCT FROM 11",
            "DELETE FROM app.dest d USING app.required r, app.pk k WHERE d.id IS NOT DISTINCT FROM r.code AND r.code = k.code",
            "UPDATE app.dest d SET label = 'x' FROM app.required r, app.pk k WHERE d.id = r.code AND r.code IS NOT DISTINCT FROM k.code"
        ];
        foreach (var sql in statements) yield return [sql];
    }

    [Theory]
    [MemberData(nameof(Positive))]
    public void NonNullSafeAndOrdinaryEqualityWithProvenKeyCannotDuplicateSources(string sql)
    {
        var catalog = Catalog();
        var stmt = One(sql, catalog);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
        Assert.Empty(stmt.Diagnostics);
        Assert.Same(catalog, stmt.CatalogAfter);
        Assert.Empty(stmt.Changes);
    }

    public static IEnumerable<object[]> Negative()
    {
        string[] statements =
        [
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE f.code IS NOT DISTINCT FROM d.tag",
            "DELETE FROM app.dest d USING app.flex f WHERE d.tag IS NOT DISTINCT FROM f.code",
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE f.code IS NOT DISTINCT FROM NULL",
            "DELETE FROM app.dest d USING app.flex f WHERE NULL IS NOT DISTINCT FROM f.code",
            "UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE p.k1 = d.id AND p.k2 IS NOT DISTINCT FROM d.tag",
            "DELETE FROM app.dest d USING app.pairs p WHERE p.k1 = d.id AND p.k2 IS NOT DISTINCT FROM NULL",
            "UPDATE app.dest d SET label = 'x' FROM app.bag b WHERE b.code IS NOT DISTINCT FROM d.id",
            "DELETE FROM app.dest d USING app.bag b WHERE b.code = d.id",
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE f.code IS DISTINCT FROM d.id",
            "DELETE FROM app.dest d USING app.flex f WHERE f.code <> d.id",
            "UPDATE app.dest d SET label = 'x' FROM app.required r WHERE d.id IS NOT DISTINCT FROM r.code OR r.note = 'x'",
            "DELETE FROM app.dest d USING app.required r WHERE d.id IS NOT DISTINCT FROM r.code OR r.code = 5",
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE d.id = f.code OR d.tag = f.code",
            "UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE d.id = d.id",
            "DELETE FROM app.dest d USING app.flex f WHERE d.tag = d.tag"
        ];
        foreach (var sql in statements) yield return [sql];
    }

    [Theory]
    [MemberData(nameof(Negative))]
    public void NullableUniqueAndNonMandatoryJoinCannotCreatePositiveMatchProof(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
    }

    [Theory]
    [InlineData("UPDATE app.dest d SET label = 'x' FROM app.flex f WHERE d.label = note")]
    [InlineData("DELETE FROM app.dest d USING app.flex f WHERE note = d.label")]
    public void AmbiguousUnqualifiedColumnCannotBeUpgradedByMatchProof(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, stmt.CatalogBinding);
        Assert.NotEqual(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
    }

    [Fact]
    public void NullablePhysicalKeyNeverFlowsThroughAnUnprovenView()
    {
        const string view = "CREATE VIEW app.v AS SELECT code, note FROM app.required;";
        var ddl = SqlCoreFacade.AnalyzeScript(Ddl + view,
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(ddl.Diagnostics);
        var stmt = One("UPDATE app.dest d SET label = 'x' FROM app.v v WHERE d.id IS NOT DISTINCT FROM v.code",
            ddl.FinalCatalog);
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
    public void TablelessQueriesKeepNotApplicableMatchProofAcrossSixProviders(SqlAgentToolType dialect)
    {
        var stmt = Assert.Single(SqlCoreFacade.AnalyzeScript("SELECT 1;",
            dialect, SqlCatalogState.KnownEmpty()).Statements);
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable, stmt.SourceMatchCardinality);
    }
}
