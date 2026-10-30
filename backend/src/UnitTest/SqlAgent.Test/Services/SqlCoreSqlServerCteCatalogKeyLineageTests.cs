using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.Core.Compilation;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Closed-world, non-executable catalog analysis of exact INT unique-key
/// lineage through SQL Server read-only CTE projections. This proof must
/// never grant CompileDml runtime authorization without independent live
/// metadata assurance.
/// </summary>
public sealed class SqlCoreSqlServerCteCatalogKeyLineageTests
{
    private const string Ddl =
        "CREATE TABLE dbo.dest (id INT NOT NULL PRIMARY KEY, label INT);" +
        "CREATE TABLE dbo.lookup (id INT NOT NULL PRIMARY KEY, payload INT);" +
        "CREATE TABLE dbo.bag (id INT NOT NULL, payload INT);" +
        "CREATE TABLE dbo.wide (id BIGINT NOT NULL PRIMARY KEY, payload INT);";

    private static SqlProviderCapabilityProfile Profile(string collation = "Latin1_General_100_CI_AS") =>
        new(SqlAgentToolType.MsSqlServer, new Version(16, 0), default, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["CATALOG_COLLATION"] = collation });

    private static SqlCatalogState Catalog()
    {
        var result = SqlCoreFacade.AnalyzeScript(Ddl,
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty(), Profile());
        Assert.Empty(result.Diagnostics);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        return result.FinalCatalog;
    }

    private static SqlStatementAnalysis One(string sql,
        SqlCatalogState? state = null, SqlProviderCapabilityProfile? profile = null)
    {
        var result = SqlCoreFacade.AnalyzeScript(sql + ";",
            SqlAgentToolType.MsSqlServer, state ?? Catalog(), profile ?? Profile());
        return Assert.Single(result.Statements);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT id, payload FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id, payload FROM dbo.lookup) DELETE FROM dbo.dest FROM picked WHERE dbo.dest.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id AS selected_id, payload FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked p WHERE dbo.dest.id = p.selected_id")]
    [InlineData("WITH picked(selected_id,payload) AS (SELECT id,payload FROM dbo.lookup) DELETE FROM dbo.dest FROM picked p WHERE dbo.dest.id = p.selected_id")]
    [InlineData("WITH seed AS (SELECT id AS sid FROM dbo.lookup), picked AS (SELECT sid AS selected_id FROM seed) UPDATE dbo.dest SET label = 1 FROM picked p WHERE dbo.dest.id = p.selected_id")]
    [InlineData("WITH seed AS (SELECT id AS sid FROM dbo.lookup), picked AS (SELECT sid AS selected_id FROM seed WHERE sid > 0) DELETE FROM dbo.dest FROM picked p WHERE dbo.dest.id = p.selected_id")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.lookup WHERE payload > 1) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id")]
    public void ExactIntPhysicalUniqueKeyLineageProvesAtMostOneMatch(string sql)
    {
        var state = Catalog();
        var stmt = One(sql, state);
        Assert.Equal(SqlScriptParseStatus.Parsed, stmt.Syntax.Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
        Assert.Empty(stmt.Diagnostics);
        Assert.Same(state, stmt.CatalogAfter);
        Assert.Empty(stmt.Changes);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT id, payload FROM dbo.bag) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.bag) DELETE FROM dbo.dest FROM picked WHERE dbo.dest.id = picked.id")]
    [InlineData("WITH picked AS (SELECT payload AS id FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id + 1 AS id FROM dbo.lookup) DELETE FROM dbo.dest FROM picked WHERE dbo.dest.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.lookup UNION ALL SELECT id FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id OR dbo.dest.label = 3")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.lookup) DELETE FROM dbo.dest FROM picked WHERE dbo.dest.id = picked.id OR dbo.dest.label = 3")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = 1")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.lookup), doubled AS (SELECT a.id FROM picked a, picked b) UPDATE dbo.dest SET label = 1 FROM doubled WHERE dbo.dest.id = doubled.id")]
    [InlineData("WITH picked AS (SELECT id FROM dbo.wide) DELETE FROM dbo.dest FROM picked WHERE dbo.dest.id = picked.id")]
    public void MissingUniqueLineageOrMandatoryEqualityNeverProvesMatch(string sql)
    {
        var stmt = One(sql);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Theory]
    [InlineData("Latin1_General_100_CS_AS")]
    [InlineData("Turkish_100_CI_AS")]
    public void UnmodeledSqlServerCollationDoesNotBorrowKeyLineage(string collation)
    {
        var stmt = One(
            "WITH picked AS (SELECT id FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id",
            profile: Profile(collation));
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
    }

    [Fact]
    public void OpenCatalogDoesNotProveCtePhysicalUniqueKeys()
    {
        var stmt = One(
            "WITH picked AS (SELECT id FROM dbo.lookup) DELETE FROM dbo.dest FROM picked WHERE dbo.dest.id = picked.id",
            state: SqlCatalogState.Unknown());
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified, stmt.SourceMatchCardinality);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnalysisEvidenceCannotBypassCompileDmlLiveMetadataBoundary(bool update)
    {
        var sql = update
            ? "WITH picked AS (SELECT id FROM dbo.lookup) UPDATE dbo.dest SET label = 1 FROM picked WHERE dbo.dest.id = picked.id"
            : "WITH picked AS (SELECT id FROM dbo.lookup) DELETE FROM dbo.dest FROM picked WHERE dbo.dest.id = picked.id";
        var stmt = One(sql);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);

        var compile = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("cte-catalog-analysis-is-not-runtime-proof",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.lookup", "dbo.dest" }));
        Assert.False(compile.Success);
        Assert.NotEmpty(compile.TypedDiagnostics);
    }

    [Fact]
    public void ExistingNonCteJoinedMutationProofIsUnaffected()
    {
        var stmt = One("UPDATE dbo.dest SET label = 1 FROM dbo.lookup s WHERE dbo.dest.id = s.id");
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne, stmt.SourceMatchCardinality);
    }
}
