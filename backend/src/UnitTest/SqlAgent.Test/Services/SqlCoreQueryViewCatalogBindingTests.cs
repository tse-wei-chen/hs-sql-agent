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
/// DQL queries may read proven PostgreSQL Virtual Catalog views by their
/// declared output names and types. A view never becomes a physical DML target,
/// and static read analysis is never SQL execution authorization.
/// </summary>
public sealed class SqlCoreQueryViewCatalogBindingTests
{
    private const string Setup =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, label TEXT, amount INTEGER);" +
        "CREATE VIEW app.v_orders (external_id, external_label, total) " +
        "AS SELECT id, label, amount FROM app.orders;" +
        "CREATE VIEW app.v_ids AS SELECT external_id AS public_id FROM app.v_orders;";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? starting = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, starting ?? SqlCatalogState.KnownEmpty());

    public static IEnumerable<object[]> SupportedQueryShapes()
    {
        string[] queries =
        [
            "SELECT external_id, external_label FROM app.v_orders",
            "SELECT v.external_id FROM app.v_orders AS v WHERE v.external_id = 1",
            "SELECT * FROM app.v_orders",
            "SELECT v.* FROM app.v_orders v",
            "SELECT public_id FROM app.v_ids",
            "WITH selected AS (SELECT external_id FROM app.v_orders) SELECT external_id FROM selected",
            "SELECT v.external_id FROM app.v_orders v JOIN app.orders o ON v.external_id = o.id",
            "SELECT external_id FROM app.v_orders WHERE EXISTS (SELECT 1 FROM app.v_ids)",
            "SELECT d.external_id FROM (SELECT external_id FROM app.v_orders) AS d"
        ];
        foreach (var query in queries) yield return [query];
    }

    [Theory]
    [MemberData(nameof(SupportedQueryShapes))]
    public void ProvenPostgresViewsAreReadableByDqlWithoutChangingCatalog(string query)
    {
        var original = Analyze(Setup);
        Assert.Empty(original.Diagnostics);
        Assert.Equal(2, original.FinalCatalog.Views.Count);
        var catalog = original.FinalCatalog;

        var analysis = Analyze(query + ";", catalog);
        var statement = Assert.Single(analysis.Statements);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.TypeBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Empty(statement.Diagnostics);
        Assert.Same(catalog, statement.CatalogBefore);
        Assert.Same(catalog, statement.CatalogAfter);
        Assert.Same(catalog, analysis.FinalCatalog);
        Assert.Empty(statement.Changes);
        Assert.Equal(2, analysis.FinalCatalog.Views.Count);
        Assert.All(statement.DetailedEffects, effect =>
        {
            Assert.Null(effect.ObjectIdentity);
            Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        });
    }

    [Theory]
    [InlineData("SELECT external_id FROM app.v_orders", SqlCatalogBindingStatus.Verified)]
    [InlineData("SELECT public_id FROM app.v_ids", SqlCatalogBindingStatus.Verified)]
    public void NewViewBecomesVisibleInTheSameOrderedScript(
        string query, SqlCatalogBindingStatus expected)
    {
        var analysis = Analyze(Setup + query + ";");
        var statement = analysis.Statements.Last();
        Assert.Equal(expected, statement.CatalogBinding);
        Assert.Empty(statement.Diagnostics);
        Assert.Equal(2, statement.CatalogBefore.Views.Count);
        Assert.Equal(2, statement.CatalogAfter.Views.Count);
        Assert.Empty(statement.Changes);
    }

    [Theory]
    [InlineData("SELECT missing FROM app.v_orders", "SQL_SCHEMA_COLUMN_NOT_FOUND")]
    [InlineData("SELECT id FROM app.v_orders", "SQL_SCHEMA_COLUMN_NOT_FOUND")]
    [InlineData("SELECT v.missing FROM app.v_orders v", "SQL_SCHEMA_COLUMN_NOT_FOUND")]
    [InlineData("SELECT external_id FROM app.v_orders v JOIN app.v_orders other ON v.external_id = other.external_id",
                "SQL_SCHEMA_COLUMN_AMBIGUOUS")]
    public void UnknownOrAmbiguousViewColumnsFailClosed(string query, string diagnosticCode)
    {
        var catalog = Analyze(Setup).FinalCatalog;
        var statement = Assert.Single(Analyze(query + ";", catalog).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Contains(statement.Diagnostics, error => error.Code == diagnosticCode);
        Assert.Same(catalog, statement.CatalogAfter);
    }

    [Theory]
    [InlineData("SELECT external_id FROM app.v_orders WHERE external_id = 'wrong'")]
    [InlineData("SELECT external_label FROM app.v_orders WHERE external_label + 1 = 2")]
    [InlineData("WITH x AS (SELECT external_id FROM app.v_orders) SELECT external_id FROM x WHERE external_id = 'wrong'")]
    public void ViewDeclaredTypeFamiliesStillRejectProvableMismatches(string query)
    {
        var catalog = Analyze(Setup).FinalCatalog;
        var statement = Assert.Single(Analyze(query + ";", catalog).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.TypeBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
        Assert.Same(catalog, statement.CatalogAfter);
    }

    [Theory]
    [InlineData("INSERT INTO app.v_orders(external_id, external_label) VALUES (1, 'unsafe')")]
    [InlineData("UPDATE app.v_orders SET external_label = 'unsafe' WHERE external_id = 1")]
    [InlineData("DELETE FROM app.v_orders WHERE external_id = 1")]
    public void ViewReadProofNeverMakesItAWritableDmlTarget(string dml)
    {
        var catalog = Analyze(Setup).FinalCatalog;
        var statement = Assert.Single(Analyze(dml + ";", catalog).Statements);
        Assert.Equal(SqlScriptStatementKind.Mutation, statement.Kind);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
        Assert.Same(catalog, statement.CatalogBefore);
        Assert.Same(catalog, statement.CatalogAfter);
        Assert.Empty(statement.Changes);
    }

    [Fact]
    public void DropViewInvalidatesQueryProofButDoesNotDropItsSourceTable()
    {
        var analysis = Analyze(Setup +
            "DROP VIEW app.v_ids;" +
            "SELECT public_id FROM app.v_ids;" +
            "SELECT external_id FROM app.v_orders;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[3].Status);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, analysis.Statements[4].CatalogBinding);
        Assert.Contains(analysis.Statements[4].Diagnostics, d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
        Assert.Equal(SqlCatalogBindingStatus.Verified, analysis.Statements[5].CatalogBinding);
        Assert.Single(analysis.FinalCatalog.Tables);
        Assert.Single(analysis.FinalCatalog.Views);
    }

    [Fact]
    public void UnknownWorldCannotProveViewNamesOrColumnTypes()
    {
        var analysis = Analyze("SELECT external_id FROM app.v_orders;",
            SqlCatalogState.Unknown());
        var statement = Assert.Single(analysis.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, statement.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ExistingPhysicalTableQueryBindingRemainsAvailableAcrossSixProviders(
        SqlAgentToolType dialect)
    {
        var analysis = Analyze("SELECT 1;", dialect: dialect);
        var statement = Assert.Single(analysis.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.TypeBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Empty(statement.Diagnostics);
    }
}
