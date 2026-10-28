using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Bounded two-relation RIGHT and FULL JOIN View null-extension:
/// right join null-extends only left; full join null-extends both.
/// All physical projections/ON/WHERE dependencies retain source ownership.</summary>
public sealed class SqlCorePostgresRightFullViewNullabilityProofTests
{
    private const string Tables =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, label TEXT, status TEXT);" +
        "CREATE TABLE app.details(id INTEGER NOT NULL, order_id INTEGER NOT NULL, amount INTEGER NOT NULL, note TEXT);" +
        "CREATE TABLE app.alternate(id INTEGER NOT NULL, order_id INTEGER NOT NULL, amount INTEGER NOT NULL);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, initial ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("RIGHT JOIN", true, false)]
    [InlineData("RIGHT OUTER JOIN", true, false)]
    [InlineData("FULL JOIN", true, true)]
    [InlineData("FULL OUTER JOIN", true, true)]
    public void JoinKindProvesExactlyWhichPhysicalSidesCanBeNullExtended(
        string join, bool leftNullable, bool rightNullable)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (public_id, detail_id, total) AS " +
            "SELECT o.id, d.id, d.amount FROM app.orders AS o " + join +
            " app.details d ON o.id = d.order_id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlCatalogOperation.CreateView,
            Assert.Single(result.Statements[3].Changes).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "detail_id", "total" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { leftNullable, rightNullable, rightNullable },
            view.Columns.Select(c => c.Nullable));
        Assert.All(view.Columns, c => Assert.Equal("INTEGER", c.TypeName));
        Assert.Equal(new[] { "app.orders", "app.details" },
            view.Dependencies.Select(d => d.DisplayName));
        Assert.Equal(new[] { "id" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "id", "amount", "order_id" }, view.SourceBindings[1].Columns);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("RIGHT JOIN", true, false)]
    [InlineData("FULL OUTER JOIN", true, true)]
    public void QualifiedWildcardCarriesCorrectOuterJoinNullableSide(
        string join, bool leftExtended, bool rightExtended)
    {
        var left = Analyze(Tables +
            "CREATE VIEW app.v (public_id) AS SELECT o.* FROM app.orders o " + join +
            " app.details d ON o.id = d.order_id;");
        Assert.Empty(left.Diagnostics);
        var view = Assert.Single(left.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "label", "status" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(leftExtended, view.Columns[0].Nullable);
        Assert.Equal(new[] { "id", "label", "status" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "order_id" }, view.SourceBindings[1].Columns);

        var right = Analyze(Tables +
            "CREATE VIEW app.v (detail_id) AS SELECT d.* FROM app.orders o " + join +
            " app.details d ON o.id = d.order_id;");
        Assert.Empty(right.Diagnostics);
        var other = Assert.Single(right.FinalCatalog.Views);
        Assert.Equal(new[] { "detail_id", "order_id", "amount", "note" },
            other.Columns.Select(c => c.Name));
        Assert.Equal(rightExtended, other.Columns[0].Nullable);
        Assert.Equal(rightExtended, other.Columns[1].Nullable);
        Assert.Equal(rightExtended, other.Columns[2].Nullable);
        Assert.True(other.Columns[3].Nullable); // Physical nullable TEXT.
        Assert.Equal(new[] { "id" }, other.SourceBindings[0].Columns);
        Assert.Equal(new[] { "id", "order_id", "amount", "note" },
            other.SourceBindings[1].Columns);
    }

    [Theory]
    [InlineData("RIGHT JOIN", true, false)]
    [InlineData("FULL JOIN", true, true)]
    public void UnqualifiedUniqueColumnsAndWhereRemainSourceOwned(string join,
        bool leftNullable, bool rightNullable)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT label, amount FROM app.orders o " + join +
            " app.details d ON d.order_id = o.id " +
            "WHERE o.status IS NULL OR d.amount > 0;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.True(view.Columns[0].Nullable); // Physical label is nullable.
        Assert.Equal(rightNullable, view.Columns[1].Nullable);
        Assert.Equal(new[] { "label", "id", "status" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "amount", "order_id" }, view.SourceBindings[1].Columns);
        Assert.Equal(leftNullable, view.Columns[0].Nullable);
    }

    [Theory]
    [InlineData("RIGHT JOIN")]
    [InlineData("FULL JOIN")]
    public void WhereNullRejectionDoesNotEraseConservativeOutputNullability(string join)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT o.id, d.id AS detail_id FROM app.orders o " + join +
            " app.details d ON o.id = d.order_id WHERE o.id IS NOT NULL;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.True(view.Columns[0].Nullable);
        Assert.Equal(join == "FULL JOIN", view.Columns[1].Nullable);
        Assert.Equal(new[] { "id" }, view.SourceBindings[0].Columns);
    }

    [Theory]
    [InlineData("RIGHT OUTER JOIN")]
    [InlineData("FULL OUTER JOIN")]
    public void JoinKeyAndWhereOnlyColumnsBlockCorrectDropTargets(string join)
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT o.label, d.amount FROM app.orders o " + join +
            " app.details d ON o.id = d.order_id WHERE o.status IS NOT NULL AND d.note IS NULL;";
        foreach (var (table, col) in new[] {
            ("app.orders", "id"), ("app.orders", "label"), ("app.orders", "status"),
            ("app.details", "order_id"), ("app.details", "amount"), ("app.details", "note")
        })
        {
            var blocked = Analyze(setup + $"ALTER TABLE {table} DROP COLUMN {col};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
            Assert.Empty(blocked.Statements[4].Changes);
            Assert.Same(blocked.Statements[4].CatalogBefore, blocked.Statements[4].CatalogAfter);
        }
        var unrelated = Analyze(setup + "ALTER TABLE app.details DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, unrelated.Statements[4].Status);
        Assert.Empty(unrelated.Diagnostics);
    }

    [Fact]
    public void FullJoinViewPropagatesBothSidesNullableToViewOfView()
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.combined (public_id, detail_id) AS " +
            "SELECT o.id, d.id FROM app.orders o FULL OUTER JOIN app.details d " +
            "ON o.id = d.order_id;" +
            "CREATE VIEW app.child AS SELECT public_id, detail_id FROM app.combined;");
        Assert.Empty(result.Diagnostics);
        var parent = result.FinalCatalog.Views.Single(v => v.Name == "app.combined");
        var child = result.FinalCatalog.Views.Single(v => v.Name == "app.child");
        Assert.Equal(new[] { true, true }, parent.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { true, true }, child.Columns.Select(c => c.Nullable));
        Assert.Equal("app.combined", Assert.Single(child.Dependencies)!.DisplayName);
    }

    [Theory]
    [InlineData("INNER JOIN", "RIGHT JOIN", true, false)]
    [InlineData("RIGHT JOIN", "FULL OUTER JOIN", true, true)]
    [InlineData("FULL JOIN", "INNER JOIN", false, false)]
    public void ReplacementReprovesCorrectNullableSidesWithoutDescendants(
        string before, string after, bool expectedLeft, bool expectedRight)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (public_id, detail_id) AS SELECT o.id, d.id " +
            "FROM app.orders o " + before + " app.details d ON o.id = d.order_id;" +
            "CREATE OR REPLACE VIEW app.v (public_id, detail_id) AS SELECT o.id, d.id " +
            "FROM app.orders o " + after + " app.details d ON o.id = d.order_id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { expectedLeft, expectedRight }, view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "app.orders", "app.details" },
            view.Dependencies.Select(d => d.DisplayName));
    }

    [Theory]
    [InlineData("INNER JOIN", "FULL JOIN")]
    [InlineData("RIGHT JOIN", "FULL JOIN")]
    [InlineData("FULL JOIN", "INNER JOIN")]
    public void CannotChangeNullableShapeWhenDescendantsWouldKeepStaleProof(
        string before, string after)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (public_id, detail_id) AS SELECT o.id, d.id " +
            "FROM app.orders o " + before + " app.details d ON o.id = d.order_id;" +
            "CREATE VIEW app.child AS SELECT public_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v (public_id, detail_id) AS SELECT o.id, d.id " +
            "FROM app.orders o " + after + " app.details d ON o.id = d.order_id;");
        var stmt = result.Statements[5];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, stmt.Status);
        Assert.Empty(stmt.Changes);
        Assert.Same(stmt.CatalogBefore, stmt.CatalogAfter);
        Assert.Equal(2, result.FinalCatalog.Views.Count);
    }

    [Theory]
    [InlineData("RIGHT JOIN")]
    [InlineData("FULL JOIN")]
    public void CascadeAndRestrictTrackBothPhysicalSources(string join)
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT o.label, d.amount FROM app.orders o " + join +
            " app.details d ON o.id = d.order_id;" +
            "CREATE VIEW app.child AS SELECT amount FROM app.v;";
        foreach (var target in new[] { "app.orders", "app.details" })
        {
            var blocked = Analyze(setup + "DROP TABLE " + target + ";");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[5].Status);
            var cascade = Analyze(setup + "DROP TABLE " + target + " CASCADE;");
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, cascade.Statements[5].Status);
            Assert.Empty(cascade.FinalCatalog.Views);
            Assert.Equal(2, cascade.FinalCatalog.Tables.Count);
        }
    }

    [Theory]
    [InlineData("SELECT o.id FROM app.orders o RIGHT JOIN app.details d USING (missing)")]
    [InlineData("SELECT o.id FROM app.orders o FULL JOIN app.details d ON o.id > d.order_id")]
    [InlineData("SELECT o.id FROM app.orders o RIGHT JOIN app.details d ON o.id = d.order_id JOIN app.alternate a ON a.id > o.id")]
    [InlineData("SELECT o.id FROM app.orders o FULL JOIN app.details d ON o.id = d.order_id OR o.id = d.id")]
    [InlineData("SELECT o.id FROM app.orders o NATURAL FULL JOIN app.details d")]
    [InlineData("SELECT o.id FROM app.orders o FULL JOIN app.orders b ON o.id = b.id")]
    public void OutsideBoundedRightFullSubsetNeverPublishesKnown(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("SELECT id FROM app.orders o RIGHT JOIN app.details d ON o.id = d.order_id")]
    [InlineData("SELECT o.id FROM app.orders o FULL JOIN app.details o ON o.id = o.order_id")]
    [InlineData("SELECT o.id FROM app.orders o RIGHT JOIN app.details d ON o.id = bad.order_id")]
    [InlineData("SELECT o.id FROM app.orders o FULL JOIN app.details d ON o.id = d.note")]
    public void AmbiguousOrInvalidRightFullBindingsRemainInvalid(string query)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + query + ";");
        var stmt = result.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, stmt.Status);
        Assert.Empty(stmt.Changes);
        Assert.Same(stmt.CatalogBefore, stmt.CatalogAfter);
        Assert.Empty(result.FinalCatalog.Views);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherFiveDialectsCannotBorrowPostgresOuterViewProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT o.id FROM app.orders o " +
            "FULL JOIN app.details d ON o.id = d.order_id;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void OpenWorldCannotProveRightFullJoin()
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT o.id FROM app.orders o FULL JOIN app.details d ON o.id = d.order_id;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
