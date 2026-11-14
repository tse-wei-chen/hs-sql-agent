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
/// Static PostgreSQL Mutation binding proves source relations independently
/// of physical write targets. View output names/types are read-only evidence,
/// never proof of an executable/authorized mutation on a View.
/// </summary>
public sealed class SqlCoreMutationViewSourceProofTests
{
    private const string Setup =
        "CREATE TABLE app.source_rows(id INTEGER NOT NULL, label TEXT);" +
        "CREATE TABLE app.dest_rows(id INTEGER NOT NULL, label TEXT);" +
        "CREATE VIEW app.v_source(read_id, read_label) AS " +
        "SELECT id, label FROM app.source_rows;" +
        "CREATE VIEW app.v_chain AS SELECT read_id AS public_id, read_label FROM app.v_source;";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, initial ?? SqlCatalogState.KnownEmpty());

    private static SqlCatalogState KnownCatalog()
    {
        var initial = Analyze(Setup);
        Assert.Empty(initial.Diagnostics);
        Assert.Equal(2, initial.FinalCatalog.Tables.Count);
        Assert.Equal(2, initial.FinalCatalog.Views.Count);
        return initial.FinalCatalog;
    }

    public static IEnumerable<object[]> ProvenReadSources()
    {
        string[] statements =
        [
            "INSERT INTO app.dest_rows(id, label) SELECT read_id, read_label FROM app.v_source",
            "INSERT INTO app.dest_rows(id, label) SELECT public_id, read_label FROM app.v_chain",
            "UPDATE app.dest_rows SET label = v.read_label FROM app.v_source v WHERE app.dest_rows.id = v.read_id",
            "DELETE FROM app.dest_rows USING app.v_source v WHERE app.dest_rows.id = v.read_id",
            "DELETE FROM app.dest_rows WHERE EXISTS (SELECT 1 FROM app.v_source WHERE read_id = 1)"
        ];
        foreach (var sql in statements) yield return [sql];
    }

    [Theory]
    [MemberData(nameof(ProvenReadSources))]
    public void ProvenViewsAreReadableInMutationSourcesWithoutPhysicalCatalogMutation(string sql)
    {
        var catalog = KnownCatalog();
        var result = Analyze(sql + ";", catalog);
        var statement = Assert.Single(result.Statements);

        Assert.Equal(SqlScriptStatementKind.Mutation, statement.Kind);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.TypeBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Empty(statement.Diagnostics);
        Assert.Same(catalog, statement.CatalogBefore);
        Assert.Same(catalog, statement.CatalogAfter);
        Assert.Same(catalog, result.FinalCatalog);
        Assert.Empty(statement.Changes);
        Assert.Equal(2, result.FinalCatalog.Views.Count);
        Assert.All(statement.DetailedEffects, effect =>
        {
            Assert.Null(effect.ObjectIdentity);
            Assert.Equal(SqlCatalogKnowledge.Unknown, effect.Certainty);
        });
    }

    [Theory]
    [InlineData("INSERT INTO app.v_source(read_id, read_label) VALUES (1, 'blocked')")]
    [InlineData("INSERT INTO app.v_source(read_id) SELECT id FROM app.source_rows")]
    [InlineData("UPDATE app.v_source SET read_label = 'blocked' WHERE read_id = 1")]
    [InlineData("DELETE FROM app.v_source WHERE read_id = 1")]
    public void ViewNeverBecomesPhysicalMutationTarget(string sql)
    {
        var catalog = KnownCatalog();
        var statement = Assert.Single(Analyze(sql + ";", catalog).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
        Assert.Same(catalog, statement.CatalogBefore);
        Assert.Same(catalog, statement.CatalogAfter);
        Assert.Empty(statement.Changes);
    }

    [Theory]
    [InlineData("INSERT INTO app.dest_rows(id) SELECT missing FROM app.v_source", "SQL_SCHEMA_COLUMN_NOT_FOUND")]
    [InlineData("UPDATE app.dest_rows SET label = v.missing FROM app.v_source v WHERE app.dest_rows.id = v.read_id", "SQL_SCHEMA_COLUMN_NOT_FOUND")]
    [InlineData("DELETE FROM app.dest_rows USING app.v_source v WHERE app.dest_rows.id = v.missing", "SQL_SCHEMA_COLUMN_NOT_FOUND")]
    [InlineData("INSERT INTO app.dest_rows(id) SELECT read_id FROM app.v_source WHERE read_id = 'bad'", "SQL_SCHEMA_TYPE_MISMATCH")]
    [InlineData("UPDATE app.dest_rows SET id = v.read_label FROM app.v_source v WHERE app.dest_rows.id = v.read_id", "SQL_SCHEMA_TYPE_MISMATCH")]
    public void UnknownAndIncompatibleReadSourceFieldsFailClosed(string sql, string expected)
    {
        var catalog = KnownCatalog();
        var statement = Assert.Single(Analyze(sql + ";", catalog).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Contains(statement.Diagnostics, d => d.Code == expected);
        if (expected == "SQL_SCHEMA_TYPE_MISMATCH")
        {
            Assert.Equal(SqlCatalogBindingStatus.Verified, statement.NameBinding);
            Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.TypeBinding);
        }
        Assert.Same(catalog, statement.CatalogAfter);
        Assert.Empty(statement.Changes);
    }

    [Fact]
    public void DroppedViewCanNoLongerBeReadByMutation()
    {
        var result = Analyze(Setup +
            "DROP VIEW app.v_chain;" +
            "INSERT INTO app.dest_rows(id) SELECT public_id FROM app.v_chain;" +
            "INSERT INTO app.dest_rows(id) SELECT read_id FROM app.v_source;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[5].CatalogBinding);
        Assert.Contains(result.Statements[5].Diagnostics, d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[6].CatalogBinding);
        Assert.Equal(2, result.FinalCatalog.Tables.Count);
        Assert.Single(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnprovenCatalogDoesNotGrantAnyReadOrWriteProof()
    {
        var result = Analyze(
            "INSERT INTO app.dest_rows(id) SELECT read_id FROM app.v_source;",
            SqlCatalogState.Unknown());
        var statement = Assert.Single(result.Statements);
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
    public void SixProviderTablelessQueryStillHasItsOriginalBindingBoundary(SqlAgentToolType dialect)
    {
        var result = Analyze("SELECT 1;", dialect: dialect);
        var statement = Assert.Single(result.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
        Assert.Empty(statement.Diagnostics);
    }
}
