using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Read-only, at-most-one-source-match proof. It cannot authorize an UPDATE,
/// claim deterministic expression evaluation, or represent actual server rows.
/// </summary>
public sealed class SqlCoreMutationSourceMatchCardinalityTests
{
    private const string Tables =
        "CREATE TABLE app.dest(id INTEGER PRIMARY KEY, label TEXT);" +
        "CREATE TABLE app.lookup(id INTEGER, label TEXT, CONSTRAINT uq_lookup UNIQUE(id));" +
        "CREATE TABLE app.extra(id INTEGER, note TEXT, CONSTRAINT uq_extra UNIQUE(id));" +
        "CREATE TABLE app.bag(id INTEGER, note TEXT);";

    private const string Composite =
        "CREATE TABLE app.dest(id INTEGER PRIMARY KEY, label TEXT);" +
        "CREATE TABLE app.pairs(k1 INTEGER, k2 INTEGER, note TEXT, " +
        "CONSTRAINT uq_pair UNIQUE(k1,k2));";

    private static SqlCatalogState Catalog(string ddl = Tables)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            ddl, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.Diagnostics);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.All(result.FinalCatalog.Tables, t => Assert.Equal(SqlCatalogKnowledge.Known, t.Knowledge));
        return result.FinalCatalog;
    }

    private static SqlStatementAnalysis One(string sql, SqlCatalogState? catalog = null) =>
        Assert.Single(SqlCoreFacade.AnalyzeScript(
            sql + ";", SqlAgentToolType.Postgres,
            catalog ?? Catalog()).Statements);

    [Theory]
    [InlineData("UPDATE app.dest d SET label = l.label FROM app.lookup l WHERE d.id = l.id")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE l.id = d.id")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE d.id = l.id AND l.label = 'x'")]
    [InlineData("UPDATE app.dest SET label = 'fixed' FROM app.lookup WHERE app.dest.id = app.lookup.id")]
    [InlineData("DELETE FROM app.dest d USING app.lookup l WHERE d.id = l.id")]
    [InlineData("DELETE FROM app.dest d USING app.lookup l WHERE l.id = d.id AND l.label = 'x'")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l, app.extra e WHERE d.id = l.id AND e.id = d.id")]
    [InlineData("DELETE FROM app.dest d USING app.lookup l, app.extra e WHERE d.id = l.id AND e.id = d.id")]
    public void ProvenUniqueSourceKeyEqualityEstablishesAtMostOneSourceMatch(string sql)
    {
        var catalog = Catalog();
        var stmt = One(sql, catalog);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
        Assert.Empty(stmt.Diagnostics);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
        Assert.Same(catalog, stmt.CatalogBefore);
        Assert.Same(catalog, stmt.CatalogAfter);
        Assert.Empty(stmt.Changes);
    }

    [Theory]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.bag b WHERE d.id = b.id")]
    [InlineData("DELETE FROM app.dest d USING app.bag b WHERE d.id = b.id")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE l.label = d.label")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE d.id = l.id OR l.label = 'x'")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE l.id > d.id")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l, app.bag b WHERE d.id = l.id AND b.id = d.id")]
    [InlineData("DELETE FROM app.dest d USING app.lookup l, app.bag b WHERE d.id = l.id AND b.id = d.id")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE l.id = l.id")]
    [InlineData("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE d.id = 1")]
    public void UnprovenSourceMultiplicityDoesNotBecomeKnownFromNameOrTypeProof(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
    }

    [Theory]
    [InlineData("UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE d.id = p.k1 AND p.k2 = 1")]
    [InlineData("DELETE FROM app.dest d USING app.pairs p WHERE d.id = p.k1 AND p.k2 = 1")]
    [InlineData("UPDATE app.dest d SET label = 'x' FROM app.pairs p WHERE d.id = p.k1")]
    public void CompositeUniqueKeyMustBeFullyConstrained(string sql)
    {
        var stmt = One(sql, Catalog(Composite));
        var expected = sql.Contains("p.k2 = 1", StringComparison.Ordinal)
            ? SqlMutationSourceMatchCardinality.ProvenAtMostOne
            : SqlMutationSourceMatchCardinality.Unverified;
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(expected, stmt.SourceMatchCardinality);
    }

    [Theory]
    [InlineData("UPDATE app.dest SET label = 'fixed' WHERE id = 1")]
    [InlineData("DELETE FROM app.dest WHERE id = 1")]
    [InlineData("INSERT INTO app.dest(id,label) VALUES (2,'x')")]
    [InlineData("SELECT id FROM app.dest")]
    public void NoJoinedMutationSourceIsNotApplicable(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable, stmt.SourceMatchCardinality);
    }

    [Fact]
    public void UnknownCatalogCannotProveCardinality()
    {
        var stmt = One("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE d.id = l.id",
            SqlCatalogState.Unknown());
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.Contains(stmt.Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
    }

    [Fact]
    public void InvalidNameOrTypeCannotBeGrantedAtMostOneProof()
    {
        var stmt = One("UPDATE app.dest d SET label = 'fixed' FROM app.lookup l WHERE d.missing = l.id");
        Assert.Equal(SqlCatalogBindingStatus.Rejected, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Fact]
    public void ViewSourcesDoNotInheritUnderlyingPhysicalUniqueKey()
    {
        const string ddl =
            Tables +
            "CREATE VIEW app.v AS SELECT id, label FROM app.lookup;";
        var stmt = One("UPDATE app.dest d SET label = v.label FROM app.v v WHERE d.id = v.id",
            Catalog(ddl));
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
    public void SixDialectTablelessQueryHasNoMutationMatchClaim(SqlAgentToolType dialect)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SELECT 1;", dialect, SqlCatalogState.KnownEmpty());
        var stmt = Assert.Single(result.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable, stmt.SourceMatchCardinality);
    }
}
