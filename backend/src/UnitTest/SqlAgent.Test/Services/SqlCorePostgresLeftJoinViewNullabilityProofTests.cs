using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL LEFT JOIN null-extends the RHS even for declared NOT NULL columns.
/// View source bindings and output shape must preserve distinct proofs.
/// </summary>
public sealed class SqlCorePostgresLeftJoinViewNullabilityProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, label TEXT);" +
        "CREATE TABLE app.details(id INTEGER NOT NULL, order_id INTEGER NOT NULL, amount INTEGER NOT NULL, note TEXT);" +
        "CREATE TABLE app.archive(id INTEGER NOT NULL, order_id INTEGER NOT NULL, amount INTEGER NOT NULL);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, initial ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("LEFT JOIN")]
    [InlineData("LEFT OUTER JOIN")]
    public void LeftJoinNullableSideOverridesDeclaredNotNull(string join)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (public_id, detail_id, total) AS " +
            "SELECT o.id, d.id, d.amount FROM app.orders o " + join +
            " app.details AS d ON o.id = d.order_id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[3].EffectCoverage);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "detail_id", "total" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { false, true, true },
            view.Columns.Select(c => c.Nullable));
        Assert.All(view.Columns, col => Assert.Equal("INTEGER", col.TypeName));
        Assert.Equal(new[] { "app.orders", "app.details" },
            view.Dependencies.Select(d => d.DisplayName));
        Assert.Equal(new[] { "id" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "id", "amount", "order_id" }, view.SourceBindings[1].Columns);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void NullExtendingRightSourceWildcardIncludesAllNullableColumns()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (public_id) AS SELECT d.* FROM app.orders o " +
            "LEFT JOIN app.details d ON o.id = d.order_id;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "order_id", "amount", "note" },
            view.Columns.Select(c => c.Name));
        Assert.All(view.Columns, c => Assert.True(c.Nullable));
        Assert.Equal(new[] { "id" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "id", "order_id", "amount", "note" },
            view.SourceBindings[1].Columns);
    }

    [Fact]
    public void NullableLeftInputRetainsOwnNullabilityAndUnqualifiedRhsResolves()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT label, amount FROM app.orders o " +
            "LEFT OUTER JOIN app.details d ON d.order_id = o.id;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "label", "amount" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { true, true }, view.Columns.Select(c => c.Nullable));
        Assert.Equal("app.orders", view.SourceBindings[0].Relation.DisplayName);
        Assert.Equal(new[] { "label", "id" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "amount", "order_id" }, view.SourceBindings[1].Columns);
    }

    [Fact]
    public void WhereRightIsNotNullDoesNotEraseConservativeOuterJoinShape()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id, d.amount FROM app.orders o " +
            "LEFT JOIN app.details d ON o.id = d.order_id WHERE d.amount IS NOT NULL;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.False(view.Columns[0].Nullable);
        Assert.True(view.Columns[1].Nullable);
        Assert.Equal(new[] { "amount", "order_id" }, view.SourceBindings[1].Columns);
    }

    [Fact]
    public void ViewOfLeftJoinCarriesNullableRightColumnsIntoNextView()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (public_id, total) AS SELECT o.id, d.amount " +
            "FROM app.orders o LEFT JOIN app.details d ON o.id = d.order_id;" +
            "CREATE VIEW app.downstream AS SELECT total FROM app.v;");
        Assert.Empty(result.Diagnostics);
        var outer = result.FinalCatalog.Views.Single(v => v.Name == "app.v");
        Assert.Equal(new[] { false, true }, outer.Columns.Select(c => c.Nullable));
        var child = result.FinalCatalog.Views.Single(v => v.Name == "app.downstream");
        Assert.True(Assert.Single(child.Columns).Nullable);
        Assert.Equal("app.v", Assert.Single(child.SourceBindings).Relation.DisplayName);
        Assert.Equal("total", Assert.Single(child.SourceColumns));
    }

    [Fact]
    public void BothSidesJoinAndWhereColumnsRemainDropProtected()
    {
        var script = Tables +
            "CREATE VIEW app.v AS SELECT o.label, d.amount FROM app.orders o " +
            "LEFT JOIN app.details d ON o.id = d.order_id WHERE d.note IS NULL;";
        foreach (var (relation, column) in new[]
        {
            ("app.orders", "id"), ("app.orders", "label"),
            ("app.details", "order_id"), ("app.details", "amount"), ("app.details", "note")
        })
        {
            var blocked = Analyze(script + $"ALTER TABLE {relation} DROP COLUMN {column};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
            Assert.Same(blocked.Statements[4].CatalogBefore, blocked.Statements[4].CatalogAfter);
        }
        var allowed = Analyze(script + "ALTER TABLE app.details DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[4].Status);
        Assert.Empty(allowed.Diagnostics);
    }

    [Fact]
    public void ReplaceInnerWithLeftReprovesNullabilityWithoutDescendants()
    {
        var script = Tables +
            "CREATE VIEW app.v (public_id, amount) AS SELECT o.id, d.amount " +
            "FROM app.orders o INNER JOIN app.details d ON o.id = d.order_id;" +
            "CREATE OR REPLACE VIEW app.v (public_id, amount) AS SELECT o.id, d.amount " +
            "FROM app.orders o LEFT OUTER JOIN app.details d ON o.id = d.order_id;";
        var result = Analyze(script);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.ReplaceView,
            Assert.Single(result.Statements[4].DetailedEffects).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { false, true }, view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "app.orders", "app.details" },
            view.Dependencies.Select(d => d.DisplayName));
    }

    [Fact]
    public void ReplaceThatChangesNullabilityCannotLeaveDependentViewsStale()
    {
        var original = Tables +
            "CREATE VIEW app.v (public_id, amount) AS SELECT o.id, d.amount " +
            "FROM app.orders o JOIN app.details d ON o.id = d.order_id;" +
            "CREATE VIEW app.child AS SELECT amount FROM app.v;";
        var result = Analyze(original +
            "CREATE OR REPLACE VIEW app.v (public_id, amount) AS " +
            "SELECT o.id, d.amount FROM app.orders o LEFT JOIN app.details d " +
            "ON o.id = d.order_id;");
        var replacement = result.Statements[5];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, replacement.Status);
        Assert.Empty(replacement.Changes);
        Assert.Same(replacement.CatalogBefore, replacement.CatalogAfter);
        Assert.Equal(2, result.FinalCatalog.Views.Count);
        Assert.False(result.FinalCatalog.Views.Single(v => v.Name == "app.child")
            .Columns.Single().Nullable);
    }

    [Fact]
    public void LeftToInnerReplacementCannotSilentlyChangeExistingDescendantContract()
    {
        var original = Tables +
            "CREATE VIEW app.v (public_id, amount) AS SELECT o.id, d.amount " +
            "FROM app.orders o LEFT JOIN app.details d ON o.id = d.order_id;" +
            "CREATE VIEW app.child AS SELECT amount FROM app.v;";
        var result = Analyze(original +
            "CREATE OR REPLACE VIEW app.v (public_id, amount) AS " +
            "SELECT o.id, d.amount FROM app.orders o INNER JOIN app.details d " +
            "ON o.id = d.order_id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[5].Status);
        Assert.Empty(result.Statements[5].Changes);
        Assert.True(result.FinalCatalog.Views.Single(v => v.Name == "app.child")
            .Columns.Single().Nullable);
    }

    [Theory]
    [InlineData("SELECT o.id, d.id FROM app.orders o LEFT JOIN app.details d USING (id)")]
    [InlineData("SELECT o.id, d.id FROM app.orders o RIGHT JOIN app.details d USING (id)")]
    [InlineData("SELECT o.id, d.id FROM app.orders o FULL JOIN app.details d ON o.id > d.order_id")]
    [InlineData("SELECT o.id, d.id FROM app.orders o LEFT JOIN app.details d ON o.id > d.order_id")]
    [InlineData("SELECT o.id, d.id FROM app.orders o LEFT JOIN app.details d ON o.id = d.order_id JOIN app.archive a USING (id)")]
    [InlineData("SELECT o.id, d.id FROM app.orders o LEFT JOIN app.details d ON o.id = d.order_id OR o.id = d.id")]
    public void OutsideBoundedLeftJoinSubsetRemainsFailClosed(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("SELECT o.id FROM app.orders o LEFT JOIN app.details o ON o.id = o.order_id")]
    [InlineData("SELECT o.id FROM app.orders o LEFT JOIN app.details d ON o.id = bad.order_id")]
    [InlineData("SELECT id FROM app.orders o LEFT JOIN app.details d ON o.id = d.order_id")]
    [InlineData("SELECT o.id FROM app.orders o LEFT JOIN app.details d ON o.id = d.note")]
    public void AmbiguousOrInvalidLeftJoinBindingCannotMintKnownView(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        var stmt = result.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, stmt.Status);
        Assert.Empty(stmt.Changes);
        Assert.Same(stmt.CatalogBefore, stmt.CatalogAfter);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NeverInheritPostgresLeftJoinViewProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o LEFT JOIN app.details d ON o.id = d.order_id;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
    }

    [Fact]
    public void OpenWorldCannotProveLeftJoinShape()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o LEFT JOIN app.details d ON o.id = d.order_id;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
