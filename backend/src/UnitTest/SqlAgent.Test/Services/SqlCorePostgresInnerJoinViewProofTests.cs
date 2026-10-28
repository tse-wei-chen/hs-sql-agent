using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Proves two-source PostgreSQL INNER JOIN View source-column
/// ownership; a flat list of SourceColumns is insufficient for DROP proofs.</summary>
public sealed class SqlCorePostgresInnerJoinViewProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, title TEXT, status TEXT);" +
        "CREATE TABLE app.lines(id INTEGER NOT NULL, order_id INTEGER NOT NULL, amount INTEGER);" +
        "CREATE TABLE app.backup(id INTEGER NOT NULL, title TEXT, status TEXT);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, initial ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN")]
    [InlineData("INNER JOIN")]
    public void TwoSourceJoinBindsColumnsToEachPhysicalRelation(string join)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.order_lines (public_id, total) AS " +
            "SELECT o.id, l.amount FROM app.orders AS o " + join +
            " app.lines AS l ON o.id = l.order_id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlCatalogOperation.CreateView, Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.CreateView,
            Assert.Single(result.Statements[3].DetailedEffects).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "total" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "app.orders", "app.lines" },
            view.Dependencies.Select(d => d.DisplayName));
        Assert.Equal(2, view.SourceBindings.Count);
        Assert.Equal("app.orders", view.SourceBindings[0].Relation.DisplayName);
        Assert.Equal(new[] { "id" }, view.SourceBindings[0].Columns);
        Assert.Equal("app.lines", view.SourceBindings[1].Relation.DisplayName);
        Assert.Equal(new[] { "amount", "order_id" }, view.SourceBindings[1].Columns);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void JoinKeyOnlyColumnIsAProtectedDependencyOfCorrectSource()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT o.title, l.amount FROM app.orders o " +
            "INNER JOIN app.lines l ON o.id = l.order_id;";
        foreach (var column in new[] { "id", "title" })
        {
            var blocked = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN " + column + ";");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
            Assert.Same(blocked.Statements[4].CatalogBefore, blocked.Statements[4].CatalogAfter);
        }
        foreach (var column in new[] { "amount", "order_id" })
        {
            var blocked = Analyze(setup + "ALTER TABLE app.lines DROP COLUMN " + column + ";");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
        }
        var allowed = Analyze(setup + "ALTER TABLE app.lines DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[4].Status);
        Assert.Empty(allowed.Diagnostics);
        var otherAllowed = Analyze(setup + "ALTER TABLE app.orders DROP COLUMN status;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, otherAllowed.Statements[4].Status);
    }

    [Fact]
    public void JoinWhereFilterOnlyDependencyTracksCorrectRelation()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.title, l.amount FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id " +
            "WHERE o.status = 'ready' AND l.amount > 0;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "title", "id", "status" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "amount", "order_id" }, view.SourceBindings[1].Columns);
        var blocked = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.title, l.amount FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id WHERE o.status = 'ready';" +
            "ALTER TABLE app.orders DROP COLUMN status;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
    }

    [Fact]
    public void ViewOfJoinAndReplaceRewireTwoSourceGraphAtomically()
    {
        var script = Tables +
            "CREATE VIEW app.v (public_id) AS SELECT o.id FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id;" +
            "CREATE VIEW app.child AS SELECT public_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v (public_id) AS SELECT b.id FROM app.backup b " +
            "INNER JOIN app.lines l ON b.id = l.order_id;";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[5].Changes).Operation);
        var v = result.FinalCatalog.Views.Single(v => v.Name == "app.v");
        Assert.Equal(new[] { "app.backup", "app.lines" },
            v.Dependencies.Select(d => d.DisplayName));
        Assert.Equal("public_id", Assert.Single(v.Columns).Name);
        Assert.Equal("app.v", Assert.Single(result.FinalCatalog.Views
            .Single(v => v.Name == "app.child").Dependencies)!.DisplayName);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(script +
            "ALTER TABLE app.orders DROP COLUMN id;").Statements[6].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(script +
            "ALTER TABLE app.backup DROP COLUMN id;").Statements[6].Status);
    }

    [Fact]
    public void EitherInputTableCanCascadeTheJoinViewAndItsDescendants()
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT o.title, l.amount FROM app.orders o " +
            "JOIN app.lines l ON o.id = l.order_id;" +
            "CREATE VIEW app.child AS SELECT title FROM app.v;";
        foreach (var source in new[] { "app.orders", "app.lines" })
        {
            var restrict = Analyze(setup + "DROP TABLE " + source + ";");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, restrict.Statements[5].Status);
            Assert.Equal(2, restrict.FinalCatalog.Views.Count);

            var cascade = Analyze(setup + "DROP TABLE " + source + " CASCADE;");
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, cascade.Statements[5].Status);
            Assert.Empty(cascade.FinalCatalog.Views);
            Assert.Equal(2, cascade.FinalCatalog.Tables.Count);
        }
    }

    [Theory]
    [InlineData("SELECT id FROM app.orders o JOIN app.lines l ON o.id = l.order_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines o ON o.id = o.order_id")]
    [InlineData("SELECT missing.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = bad.order_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.missing")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.title = l.order_id")]
    public void AmbiguousDuplicateUnboundOrTypeIncompatibleJoinCannotMintCatalog(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Same(result.Statements[3].CatalogBefore, result.Statements[3].CatalogAfter);
        Assert.Empty(result.FinalCatalog.Views);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("SELECT o.id FROM app.orders o FULL JOIN app.lines l ON o.id > l.order_id")]
    [InlineData("SELECT o.id FROM app.orders o RIGHT JOIN app.lines l USING (missing)")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l USING (missing)")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id > l.order_id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id CROSS JOIN app.backup b")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id OR o.title = l.id")]
    [InlineData("SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id WHERE l.amount + 1 = 10")]
    public void UnmodeledJoinGrammarRemainsFailClosed(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonPostgresCannotInheritJoinProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenWorldCannotProveJoinSourceIdentities()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o JOIN app.lines l ON o.id = l.order_id;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
