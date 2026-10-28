using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL CREATE VIEW (explicit output columns): the header renames the
/// query's visible projection, never the physical columns read from source.
/// Verifies known-Catalog atomicity, replacement prefix, and dialect isolation.
/// </summary>
public sealed class SqlCorePostgresViewOutputColumnHeaderProofTests
{
    private const string Base =
        "CREATE TABLE app.src(id INTEGER NOT NULL, label TEXT, extra INTEGER);" +
        "CREATE TABLE app.alt(id INTEGER NOT NULL, label TEXT, extra INTEGER);";

    private static SqlScriptAnalysisResult Analyze(string script,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres,
        SqlCatalogState? initial = null) =>
        SqlCoreFacade.AnalyzeScript(script, dialect, initial ?? SqlCatalogState.KnownEmpty());

    [Fact]
    public void ExplicitHeaderOverridesDirectSelectAliasesAndPreservesSourceAndTypes()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v (public_id, public_label) " +
            "AS SELECT id AS ignored_id, label AS ignored_label FROM app.src;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        var change = Assert.Single(result.Statements[2].Changes);
        Assert.Equal(SqlCatalogOperation.CreateView, change.Operation);
        Assert.Equal(SqlStatementEffectOperation.CreateView,
            Assert.Single(result.Statements[2].DetailedEffects).Operation);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[2].EffectCoverage);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "public_label" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "label" }, view.SourceColumns);
        Assert.Equal("INTEGER", view.Columns[0].TypeName);
        Assert.False(view.Columns[0].Nullable);
        Assert.Equal("TEXT", view.Columns[1].TypeName);
        Assert.True(view.Columns[1].Nullable);
        Assert.Equal("app.src", Assert.Single(view.Dependencies).DisplayName);
    }

    [Fact]
    public void HeaderOverridesWildcardColumnsAndEnablesViewChaining()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v (public_id, description, detail) AS SELECT * FROM app.src;" +
            "CREATE VIEW app.child (child_id) AS SELECT public_id FROM app.v;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.FinalCatalog.Views.Count);
        var v = result.FinalCatalog.Views.Single(x => x.Name == "app.v");
        Assert.Equal(new[] { "public_id", "description", "detail" },
            v.Columns.Select(x => x.Name));
        Assert.Equal(new[] { "id", "label", "extra" }, v.SourceColumns);
        var child = result.FinalCatalog.Views.Single(x => x.Name == "app.child");
        Assert.Equal("child_id", Assert.Single(child.Columns).Name);
        Assert.Equal("public_id", Assert.Single(child.SourceColumns));
        Assert.Equal("app.v", Assert.Single(child.Dependencies).DisplayName);
    }

    [Fact]
    public void HeaderAllowsRepeatedSourceOrQueryAliasesWhenFinalOutputsUnique()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v (first_id, second_id) " +
            "AS SELECT id AS same_alias, id AS same_alias FROM app.src;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var v = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "first_id", "second_id" }, v.Columns.Select(x => x.Name));
        Assert.Equal(new[] { "id", "id" }, v.SourceColumns);
    }

    [Fact]
    public void PhysicalDependencyUsesOriginalColumnNotExplicitHeaderName()
    {
        var setup = Base + "CREATE VIEW app.v (external_id) AS SELECT id FROM app.src;";
        var blocked = Analyze(setup + "ALTER TABLE app.src DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[3].Status);
        Assert.Empty(blocked.Statements[3].Changes);
        Assert.Same(blocked.Statements[3].CatalogBefore,
                    blocked.Statements[3].CatalogAfter);

        var permitted = Analyze(setup + "ALTER TABLE app.src DROP COLUMN label;");
        Assert.Empty(permitted.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, permitted.Statements[3].Status);
        Assert.Equal(new[] { "id", "extra" }, permitted.FinalCatalog.Tables
            .Single(x => x.Name == "app.src").Columns.Select(x => x.Name));
    }

    [Theory]
    [InlineData("(single)", "SELECT id, label FROM app.src")]
    [InlineData("(one, two, three)", "SELECT id, label FROM app.src")]
    [InlineData("(single)", "SELECT * FROM app.src")]
    [InlineData("(a, b, c, d)", "SELECT * FROM app.src")]
    [InlineData("(same, same)", "SELECT id, label FROM app.src")]
    [InlineData("(same, SAME)", "SELECT id, label FROM app.src")]
    [InlineData("(first, second)", "SELECT id, missing FROM app.src")]
    public void MismatchedDuplicateOrUnboundHeaderNeverChangesKnownCatalog(
        string header, string select)
    {
        var result = Analyze(Base + "CREATE VIEW app.v " + header + " AS " + select + ";");
        var statement = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Empty(statement.Changes);
        Assert.Same(statement.CatalogBefore, statement.CatalogAfter);
        Assert.Empty(result.FinalCatalog.Views);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void HeaderSupportsReplacingExistingViewAndTrailingExtension()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v (public_id) AS SELECT id FROM app.src;" +
            "CREATE VIEW app.consumer AS SELECT public_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v (public_id, public_label) " +
            "AS SELECT id AS ignored_id, label AS ignored_label FROM app.alt;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.ReplaceView,
            Assert.Single(result.Statements[4].DetailedEffects).Operation);
        var v = result.FinalCatalog.Views.Single(x => x.Name == "app.v");
        Assert.Equal(new[] { "public_id", "public_label" }, v.Columns.Select(x => x.Name));
        Assert.Equal("app.alt", Assert.Single(v.Dependencies).DisplayName);
        Assert.Equal(new[] { "id", "label" }, v.SourceColumns);
        Assert.Equal("public_id", Assert.Single(result.FinalCatalog.Views
            .Single(x => x.Name == "app.consumer").Columns).Name);
    }

    [Theory]
    [InlineData("(renamed)", "SELECT id FROM app.alt")]
    [InlineData("(public_id)", "SELECT label FROM app.alt")]
    [InlineData("(public_id, extra)", "SELECT label, id FROM app.alt")]
    [InlineData("(public_id, public_id)", "SELECT id, label FROM app.alt")]
    public void IncompatibleReplacementHeaderRemainsAtomic(string header, string select)
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v (public_id) AS SELECT id FROM app.src;" +
            "CREATE OR REPLACE VIEW app.v " + header + " AS " + select + ";");
        var replacement = result.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, replacement.Status);
        Assert.Empty(replacement.Changes);
        Assert.Same(replacement.CatalogBefore, replacement.CatalogAfter);
        var old = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal("public_id", Assert.Single(old.Columns).Name);
        Assert.Equal("app.src", Assert.Single(old.Dependencies).DisplayName);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void NoHeaderStillRejectsDuplicateQueryOutputAliases()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v AS SELECT id AS repeated, id AS repeated FROM app.src;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void HeaderOnOpenWorldCannotBecomeKnown()
    {
        var result = Analyze(
            "CREATE VIEW app.v (visible) AS SELECT id FROM app.src;",
            initial: SqlCatalogState.Unknown());
        var statement = Assert.Single(result.Statements);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.NotEqual(SqlCatalogKnowledge.Known, statement.Certainty);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ExplicitHeaderDoesNotAccidentallyProveOtherProviderDdl(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v (visible) AS SELECT id FROM app.src;",
            dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("CREATE VIEW app.v (visible) AS SELECT id FROM app.src WHERE id > 1;")]
    [InlineData("CREATE VIEW app.v (visible) AS SELECT id+1 FROM app.src;")]
    [InlineData("CREATE VIEW app.v (visible) WITH (security_barrier=true) AS SELECT id FROM app.src;")]
    public void UnsupportedViewOptionsOrExpressionsDoNotMintProvenCatalog(string statement)
    {
        var result = Analyze(Base + statement);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            result.Statements[2].Status);
        Assert.Empty(result.FinalCatalog.Views);
    }
}
