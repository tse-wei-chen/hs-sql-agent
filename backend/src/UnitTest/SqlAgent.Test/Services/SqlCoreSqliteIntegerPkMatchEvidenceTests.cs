using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQLite dynamic affinity must never borrow PostgreSQL's generic UNIQUE
/// equality assumptions. Static integer PRIMARY KEY match evidence is
/// independent of executable authorization and SQLite type verification.
/// </summary>
public sealed class SqlCoreSqliteIntegerPkMatchEvidenceTests
{
    private const string Ddl =
        "CREATE TABLE dest (id INTEGER PRIMARY KEY, bucket INTEGER, label INTEGER);" +
        "CREATE TABLE lookup (id INTEGER PRIMARY KEY, bridge INTEGER, payload INTEGER);" +
        "CREATE TABLE further (id INTEGER PRIMARY KEY, bridge INTEGER);" +
        "CREATE TABLE bag (id INTEGER, payload INTEGER);" +
        "CREATE TABLE intkey (id INT PRIMARY KEY, payload INTEGER);" +
        "CREATE TABLE textkey (id TEXT PRIMARY KEY, payload INTEGER);" +
        "CREATE TABLE uniquekey (id INTEGER UNIQUE, payload INTEGER);" +
        "CREATE TABLE composite (k1 INTEGER, k2 INTEGER, payload INTEGER, PRIMARY KEY(k1,k2));";

    private static SqlProviderCapabilityProfile Profile(int minor = 35) =>
        new(SqlAgentToolType.Sqlite, new Version(3, minor));

    private static SqlCatalogState Catalog()
    {
        var result = SqlCoreFacade.AnalyzeScript(Ddl, SqlAgentToolType.Sqlite,
            SqlCatalogState.KnownEmpty(), Profile());
        Assert.Empty(result.Diagnostics);
        Assert.Equal(8, result.FinalCatalog.Tables.Count);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        return result.FinalCatalog;
    }

    private static SqlStatementAnalysis One(string sql, SqlCatalogState? catalog = null,
        SqlProviderCapabilityProfile? profile = null) =>
        Assert.Single(SqlCoreFacade.AnalyzeScript(sql + ";", SqlAgentToolType.Sqlite,
            catalog ?? Catalog(), profile ?? Profile()).Statements);

    [Theory]
    [InlineData("UPDATE dest SET label = s.payload FROM lookup s WHERE dest.id = s.id")]
    [InlineData("UPDATE dest SET label = s.payload FROM lookup s WHERE s.id = dest.id AND s.payload = 10")]
    [InlineData("UPDATE dest SET label = s.payload FROM lookup s WHERE s.id = dest.bucket")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE s.id = 42")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE 42 = s.id")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE dest.id = s.bridge AND s.bridge = s.id")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s, further f WHERE dest.id = s.id AND f.id = s.id")]
    public void ExactIntegerPrimaryKeysCanEstablishAtMostOneSourceMatch(string sql)
    {
        var state = Catalog();
        var statement = One(sql, catalog: state);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, statement.TypeBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, statement.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne,
            statement.SourceMatchCardinality);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Empty(statement.Changes);
        Assert.Same(state, statement.CatalogAfter);
    }

    [Theory]
    [InlineData("UPDATE dest SET label = 1 FROM bag s WHERE dest.id = s.id")]
    [InlineData("UPDATE dest SET label = 1 FROM intkey s WHERE dest.id = s.id")]
    [InlineData("UPDATE dest SET label = 1 FROM textkey s WHERE dest.id = s.id")]
    [InlineData("UPDATE dest SET label = 1 FROM uniquekey s WHERE dest.id = s.id")]
    [InlineData("UPDATE dest SET label = 1 FROM composite s WHERE dest.id = s.k1 AND s.k2 = 3")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE dest.id = s.bridge")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE s.id = s.bridge")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE s.id = '1'")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE dest.id = s.id AND 'é' = 'é'")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE s.id = 1.5")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s WHERE dest.id = s.id OR s.id = 2")]
    [InlineData("UPDATE dest SET label = 1 FROM lookup s, bag b WHERE dest.id = s.id AND b.id = s.id")]
    public void UnprovenAffinitiesKeysOrMultipleSourcesStayUnverified(string sql)
    {
        var statement = One(sql);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified,
            statement.SourceMatchCardinality);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, statement.Status);
    }

    [Fact]
    public void OpenOrIncompleteCatalogNeverCreatesPhysicalKeyProof()
    {
        var stmt = One("UPDATE dest SET label = 1 FROM lookup s WHERE dest.id = s.id",
            catalog: SqlCatalogState.Unknown());
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
    }

    [Fact]
    public void PreUpdateFromVersionCannotClaimEvidence()
    {
        const string sql = "UPDATE dest SET label = 1 FROM lookup s WHERE dest.id = s.id";
        var stmt = One(sql, profile: Profile(32));
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, stmt.Syntax.Status);
    }

    [Fact]
    public void StaticEvidenceNeverAuthorizesCtePrefixedPhysicalUpdateFrom()
    {
        const string sql =
            "WITH picked AS (SELECT 2 AS id) " +
            "UPDATE dest SET label = s.payload FROM lookup s WHERE dest.id = s.id";
        var attempt = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-pk-analysis-not-execution"));
        Assert.False(attempt.Success);
        Assert.NotEmpty(attempt.TypedDiagnostics);
    }

    [Theory]
    [InlineData("UPDATE dest SET label = 1 WHERE id = 1")]
    [InlineData("DELETE FROM dest WHERE id = 1")]
    [InlineData("INSERT INTO dest(id,label) VALUES (2,5)")]
    [InlineData("SELECT id FROM dest")]
    public void NonJoinedStatementDoesNotInventMatchProof(string sql)
    {
        Assert.Equal(SqlMutationSourceMatchCardinality.NotApplicable,
            One(sql).SourceMatchCardinality);
    }
}
