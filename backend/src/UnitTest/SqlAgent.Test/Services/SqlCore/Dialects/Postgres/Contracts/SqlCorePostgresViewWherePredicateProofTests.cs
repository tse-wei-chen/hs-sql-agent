using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A WHERE expression used by a PostgreSQL View can depend on columns
/// absent from SELECT output. Every proven predicate must contribute its
/// actual source-column dependency to Virtual Catalog before publishing Known.
/// </summary>
public sealed class SqlCorePostgresViewWherePredicateProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, status TEXT, qty INTEGER, deleted BOOLEAN);" +
        "CREATE TABLE app.alternate(id INTEGER NOT NULL, status TEXT, qty INTEGER, deleted BOOLEAN);";
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, state ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("WHERE qty >= 10")]
    [InlineData("WHERE o.qty >= 10")]
    [InlineData("WHERE status = 'ready'")]
    [InlineData("WHERE o.status <> 'cancelled'")]
    [InlineData("WHERE qty < -1")]
    [InlineData("WHERE deleted = TRUE")]
    [InlineData("WHERE deleted <> FALSE")]
    [InlineData("WHERE status IS NULL")]
    [InlineData("WHERE o.status IS NOT NULL")]
    [InlineData("WHERE qty != 3")]
    public void PredicateColumnsAreDependenciesEvenWhenNotProjected(string predicate)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders AS o " + predicate + ";");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogOperation.CreateView,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[2].EffectCoverage);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal("id", Assert.Single(view.Columns).Name);
        Assert.Equal("app.orders", Assert.Single(view.Dependencies)!.DisplayName);
        Assert.Contains("id", view.SourceColumns);
        var predicateColumn = predicate.Contains("qty", StringComparison.Ordinal) ? "qty"
            : predicate.Contains("status", StringComparison.Ordinal) ? "status" : "deleted";
        Assert.Contains(predicateColumn, view.SourceColumns);
        Assert.Equal(2, view.SourceColumns.Count);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("WHERE qty = 1 AND status IS NOT NULL")]
    [InlineData("WHERE qty = 1 OR status IS NULL")]
    [InlineData("WHERE (qty = 1 OR qty = 2) AND status = 'ready'")]
    [InlineData("WHERE (qty > 0 AND (status = 'ready' OR deleted IS NULL))")]
    public void ConjunctionDisjunctionAndGroupingRetainAllPhysicalDependencies(string predicate)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " +
            "SELECT o.id FROM app.orders AS o " + predicate + ";");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Contains("qty", view.SourceColumns);
        if (predicate.Contains("status", StringComparison.Ordinal))
            Assert.Contains("status", view.SourceColumns);
        if (predicate.Contains("deleted", StringComparison.Ordinal))
            Assert.Contains("deleted", view.SourceColumns);
        Assert.Equal(view.SourceColumns.Count, view.SourceColumns.Distinct().Count());
    }

    [Fact]
    public void FilterOnlyColumnCannotBeDroppedButUnreferencedColumnCan()
    {
        var setup = Tables +
            "CREATE VIEW app.active_ids (external_id) AS " +
            "SELECT o.id FROM app.orders AS o WHERE o.status = 'ready' AND o.qty > 0;";
        var blocked = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN status;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[3].Status);
        Assert.Empty(blocked.Statements[3].Changes);
        Assert.Same(blocked.Statements[3].CatalogBefore, blocked.Statements[3].CatalogAfter);
        Assert.Equal(4, blocked.FinalCatalog.Tables.Single(x => x.Name == "app.orders").Columns.Count);

        var allowed = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN deleted;");
        Assert.Empty(allowed.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[3].Status);
        Assert.Equal(3, allowed.FinalCatalog.Tables.Single(x => x.Name == "app.orders").Columns.Count);
    }

    [Fact]
    public void FilteredViewOfViewHasSourceContractAndDependencyChain()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.base AS SELECT id, qty FROM app.orders WHERE qty > 0;" +
            "CREATE VIEW app.filtered (public_id) AS " +
            "SELECT b.id FROM app.base b WHERE b.qty >= 2;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        var filtered = result.FinalCatalog.Views.Single(x => x.Name == "app.filtered");
        Assert.Equal("public_id", Assert.Single(filtered.Columns).Name);
        Assert.Equal("app.base", Assert.Single(filtered.Dependencies)!.DisplayName);
        Assert.Equal(new[] { "id", "qty" }, filtered.SourceColumns);
    }

    [Fact]
    public void ReplaceViewRewiresFilterDependenciesToNewSourceAndPreservesOutput()
    {
        var setup = Tables +
            "CREATE VIEW app.v (public_id) AS SELECT id FROM app.orders WHERE qty > 0;" +
            "CREATE VIEW app.child AS SELECT public_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v (public_id) AS " +
            "SELECT a.id FROM app.alternate a WHERE a.status = 'ready';";
        var result = Analyze(setup);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.ReplaceView,
            Assert.Single(result.Statements[4].DetailedEffects).Operation);
        var view = result.FinalCatalog.Views.Single(x => x.Name == "app.v");
        Assert.Equal("public_id", Assert.Single(view.Columns).Name);
        Assert.Equal("app.alternate", Assert.Single(view.Dependencies)!.DisplayName);
        Assert.Equal(new[] { "id", "status" }, view.SourceColumns);
        Assert.Equal("app.v", Assert.Single(result.FinalCatalog.Views
            .Single(x => x.Name == "app.child").Dependencies)!.DisplayName);

        var oldDrop = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN qty;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, oldDrop.Statements[5].Status);
        var newDrop = Analyze(setup + "ALTER TABLE app.alternate DROP COLUMN status;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, newDrop.Statements[5].Status);
        Assert.Empty(newDrop.Statements[5].Changes);
    }

    [Theory]
    [InlineData("WHERE unknown = 1")]
    [InlineData("WHERE o.unknown = 1")]
    [InlineData("WHERE missing.qty = 1")]
    [InlineData("WHERE orders.qty = 1")]
    [InlineData("WHERE qty = 1 AND missing.status IS NULL")]
    public void UnboundPredicateColumnCannotPublishKnownCatalog(string predicate)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o " + predicate + ";");
        var statement = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Empty(statement.Changes);
        Assert.Same(statement.CatalogBefore, statement.CatalogAfter);
        Assert.Empty(result.FinalCatalog.Views);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("WHERE id IN (1, 2)")]
    [InlineData("WHERE qty BETWEEN 1 AND 10")]
    [InlineData("WHERE status LIKE 'A%'")]
    [InlineData("WHERE NOT deleted")]
    [InlineData("WHERE qty + 1 = 2")]
    [InlineData("WHERE qty = 1 ORDER BY id")]
    [InlineData("WHERE EXISTS (SELECT 1)")]
    [InlineData("WHERE id = qty")]
    [InlineData("WHERE status = 1")]
    [InlineData("WHERE qty = 'one'")]
    public void UnsupportedPredicateGrammarOrUnprovenTypeCannotPublishKnownCatalog(
        string predicate)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS SELECT id FROM app.orders " +
            predicate + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnclosedGroupingIsRejectedAtTheLexicalBoundary()
    {
        // The lexer reports malformed grouping before Script analysis and
        // must not be misclassified as a supported or silently partial View.
        Assert.Throws<HsSqlAgent.SqlCore.SqlParsing.SqlParseException>(() =>
            Analyze(Tables +
                "CREATE VIEW app.v AS SELECT id FROM app.orders WHERE (qty = 1;"));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NeverPromotesOtherDialectViewWhereProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT id FROM app.orders WHERE id = 1;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenWorldCannotProveFilteredView()
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT id FROM app.orders WHERE id = 1;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
