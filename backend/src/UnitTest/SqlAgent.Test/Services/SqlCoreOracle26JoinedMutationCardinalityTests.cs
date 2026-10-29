using System;
using System.Collections.Generic;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Oracle 26 direct-join UPDATE can raise ORA-30926 on repeated target
/// matches; direct-join DELETE may tolerate repeats. This is exclusively
/// static at-most-one source-combination evidence, never a live error proof.
/// </summary>
public sealed class SqlCoreOracle26JoinedMutationCardinalityTests
{
    private const string Ddl =
        "CREATE TABLE dest(id NUMBER NOT NULL PRIMARY KEY, bucket NUMBER, payload NUMBER);" +
        "CREATE TABLE lookup(id NUMBER NOT NULL PRIMARY KEY, bridge NUMBER, payload NUMBER);" +
        "CREATE TABLE extra(id NUMBER NOT NULL PRIMARY KEY, bridge NUMBER);" +
        "CREATE TABLE bag(id NUMBER, bridge NUMBER);" +
        "CREATE TABLE textkey(id VARCHAR2(32) NOT NULL PRIMARY KEY, bridge NUMBER);" +
        "CREATE TABLE comp(k1 NUMBER, k2 NUMBER, bridge NUMBER, CONSTRAINT comp_uk UNIQUE(k1,k2));";

    private static SqlProviderCapabilityProfile Oracle26() =>
        new(SqlAgentToolType.Oracle, ServerVersion: new Version(26, 0));

    private static SqlProviderCapabilityProfile Oracle23() =>
        new(SqlAgentToolType.Oracle, ServerVersion: new Version(23, 0));

    private static SqlCatalogState Catalog()
    {
        var ddl = SqlCoreFacade.AnalyzeScript(Ddl, SqlAgentToolType.Oracle,
            SqlCatalogState.KnownEmpty());
        Assert.Empty(ddl.Diagnostics);
        Assert.True(ddl.FinalCatalog.IsClosedWorld);
        Assert.Equal(6, ddl.FinalCatalog.Tables.Count);
        return ddl.FinalCatalog;
    }

    private static SqlStatementAnalysis One(
        string sql, SqlProviderCapabilityProfile? profile = null,
        SqlCatalogState? state = null) =>
        Assert.Single(SqlCoreFacade.AnalyzeScript(sql + ";", SqlAgentToolType.Oracle,
            state ?? Catalog(), profile ?? Oracle26()).Statements);

    public static IEnumerable<object[]> Positive()
    {
        string[] statements =
        [
            "UPDATE dest SET payload = s.payload FROM lookup s WHERE dest.id = s.id",
            "DELETE FROM dest FROM lookup s WHERE s.id = dest.id",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE s.id = 17",
            "DELETE FROM dest FROM lookup s WHERE 17 = s.id",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE dest.id = s.bridge AND s.bridge = s.id",
            "DELETE FROM dest FROM lookup s WHERE s.id = s.bridge AND s.bridge = dest.id",
            "UPDATE dest SET payload = 1 FROM lookup s, extra e WHERE dest.id = s.id AND s.id = e.id",
            "DELETE FROM dest FROM lookup s, extra e WHERE dest.id = s.id AND s.id = e.id",
            "UPDATE dest SET payload = 1 FROM lookup s, extra e WHERE dest.id = s.bridge AND s.bridge = s.id AND s.id = e.bridge AND e.bridge = e.id",
            "DELETE FROM dest FROM lookup s, extra e WHERE e.id = e.bridge AND e.bridge = s.id AND s.id = s.bridge AND s.bridge = dest.id",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE s.id = dest.bucket",
            "DELETE FROM dest FROM lookup s WHERE s.id = s.bridge AND s.bridge = 42",
            "UPDATE dest SET payload = 1 FROM comp c WHERE c.k1 = dest.id AND c.k2 = 2",
            "DELETE FROM dest FROM comp c WHERE c.k1 = c.bridge AND c.bridge = dest.id AND c.k2 = 2"
        ];
        foreach (var statement in statements) yield return [statement];
    }

    [Theory]
    [MemberData(nameof(Positive))]
    public void Version26AndBareNumberUniqueEqualityProveAtMostOneSource(string sql)
    {
        var state = Catalog();
        var stmt = One(sql, state: state);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlEffectCoverage.Partial, stmt.EffectCoverage);
        Assert.Empty(stmt.Diagnostics);
        Assert.Empty(stmt.Changes);
        Assert.Same(state, stmt.CatalogAfter);
    }

    public static IEnumerable<object[]> Negative()
    {
        string[] statements =
        [
            "UPDATE dest SET payload = 1 FROM bag b WHERE dest.id = b.id",
            "DELETE FROM dest FROM bag b WHERE b.id = dest.id",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE dest.id = s.bridge",
            "DELETE FROM dest FROM lookup s WHERE s.bridge = dest.id",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE s.id = s.bridge",
            "DELETE FROM dest FROM lookup s WHERE s.id = s.bridge",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE dest.id = s.bridge AND (s.bridge = s.id OR s.payload = 4)",
            "DELETE FROM dest FROM lookup s WHERE dest.id = s.id OR s.id = 2",
            "UPDATE dest SET payload = 1 FROM lookup s, bag b WHERE dest.id = s.id AND b.id = s.id",
            "DELETE FROM dest FROM lookup s, bag b WHERE dest.id = s.id AND b.id = s.id",
            "UPDATE dest SET payload = 1 FROM comp c WHERE c.k1 = dest.id",
            "DELETE FROM dest FROM comp c WHERE c.k1 = dest.id AND c.k2 = c.bridge",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE s.id = '1'",
            "DELETE FROM dest FROM lookup s WHERE s.id = 1.5",
            "UPDATE dest SET payload = 1 FROM textkey t WHERE dest.id = t.id",
            "DELETE FROM dest FROM textkey t WHERE dest.id = t.id",
            "UPDATE dest SET payload = 1 FROM lookup s WHERE s.id > dest.id",
            "DELETE FROM dest FROM lookup s WHERE NOT (s.id = dest.id)"
        ];
        foreach (var statement in statements) yield return [statement];
    }

    [Theory]
    [MemberData(nameof(Negative))]
    public void OrUnanchoredNonUniqueAndNonNumberNeverMakePositiveProof(string sql)
    {
        var stmt = One(sql);
        Assert.NotEqual(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
        Assert.True(stmt.CatalogBinding is SqlCatalogBindingStatus.Verified
            or SqlCatalogBindingStatus.Rejected);
        Assert.True(stmt.Status is SqlStatementAnalysisStatus.PartiallyAnalyzed
            or SqlStatementAnalysisStatus.Invalid);
    }

    [Theory]
    [InlineData("UPDATE dest SET payload = 1 FROM lookup s WHERE dest.id = s.id")]
    [InlineData("DELETE FROM dest FROM lookup s WHERE dest.id = s.id")]
    public void OlderOrUnspecifiedOracleVersionCannotInheritJoinProof(string sql)
    {
        foreach (var profile in new SqlProviderCapabilityProfile?[] { null, Oracle23() })
        {
            var stmt = Assert.Single(SqlCoreFacade.AnalyzeScript(sql + ";",
                SqlAgentToolType.Oracle, Catalog(), profile).Statements);
            Assert.NotEqual(SqlMutationSourceMatchCardinality.ProvenAtMostOne,
                stmt.SourceMatchCardinality);
        }
    }

    [Fact]
    public void OpenWorldCannotMintOracle26UniqueMatchEvidence()
    {
        var stmt = One("UPDATE dest SET payload = 1 FROM lookup s WHERE dest.id = s.id",
            state: SqlCatalogState.Unknown());
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Theory]
    [InlineData("UPDATE dest SET payload = 1 WHERE id = 1")]
    [InlineData("DELETE FROM dest WHERE id = 1")]
    [InlineData("INSERT INTO dest(id,payload) VALUES (1,1)")]
    [InlineData("SELECT id FROM dest")]
    public void NoAuxiliarySourceIsNotApplicable(string sql)
    {
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable,
            One(sql).SourceMatchCardinality);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void SixProviderTablelessQueriesKeepNotApplicable(SqlAgentToolType provider)
    {
        var stmt = Assert.Single(SqlCoreFacade.AnalyzeScript("SELECT 1;", provider,
            SqlCatalogState.KnownEmpty()).Statements);
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable, stmt.SourceMatchCardinality);
    }
}
