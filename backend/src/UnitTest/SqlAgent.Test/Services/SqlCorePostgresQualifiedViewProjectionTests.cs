using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL single-source qualified View projection proof. Qualifiers are
/// bindings to one declared FROM source, never standalone Catalog identities
/// or SQL authorization.
/// </summary>
public sealed class SqlCorePostgresQualifiedViewProjectionTests
{
    private const string Tables =
        "CREATE TABLE app.src(id INTEGER NOT NULL, label TEXT, extra INTEGER);" +
        "CREATE TABLE app.alt(id INTEGER NOT NULL, label TEXT, extra INTEGER);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? initial = null,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect,
            initial ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("SELECT s.id, s.label FROM app.src AS s")]
    [InlineData("SELECT s.id, s.label FROM app.src s")]
    [InlineData("SELECT src.id, src.label FROM app.src")]
    [InlineData("SELECT s.id, label FROM app.src AS s")]
    [InlineData("SELECT id, s.label FROM app.src AS s")]
    public void QualifiedColumnsResolveSingleDeclaredFromBinding(string select)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + select + ";");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogOperation.CreateView,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[2].EffectCoverage);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "label" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "label" }, view.SourceColumns);
        Assert.Equal("INTEGER", view.Columns[0].TypeName);
        Assert.False(view.Columns[0].Nullable);
        Assert.Equal("TEXT", view.Columns[1].TypeName);
        Assert.Equal("app.src", Assert.Single(view.Dependencies)!.DisplayName);
    }

    [Theory]
    [InlineData("SELECT s.* FROM app.src AS s")]
    [InlineData("SELECT s.* FROM app.src s")]
    [InlineData("SELECT src.* FROM app.src")]
    [InlineData("SELECT * FROM app.src AS s")]
    public void QualifiedOrOrdinaryWildcardExpandsBoundSource(string select)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v (public_id) AS " + select + ";");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "public_id", "label", "extra" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "label", "extra" }, view.SourceColumns);
    }

    [Fact]
    public void SourceColumnsStayPhysicalUnderQualifiedSelectAndDualAliasing()
    {
        var setup = Tables +
            "CREATE VIEW app.v (public_id) AS " +
            "SELECT s.id AS intermediate FROM app.src AS s;" +
            "CREATE VIEW app.child (child_id) AS SELECT v.public_id FROM app.v AS v;";
        var proven = Analyze(setup);
        Assert.Empty(proven.Diagnostics);
        Assert.Equal("id", Assert.Single(proven.FinalCatalog.Views
            .Single(v => v.Name == "app.v").SourceColumns));
        Assert.Equal("public_id", Assert.Single(proven.FinalCatalog.Views
            .Single(v => v.Name == "app.child").SourceColumns));

        var blocked = Analyze(setup + "ALTER TABLE app.src DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[4].Status);
        Assert.Empty(blocked.Statements[4].Changes);
        Assert.Same(blocked.Statements[4].CatalogBefore,
                    blocked.Statements[4].CatalogAfter);

        var allowed = Analyze(setup + "ALTER TABLE app.src DROP COLUMN label;");
        Assert.Empty(allowed.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[4].Status);
    }

    [Fact]
    public void ReplacingQualifiedViewRebindsToNewPhysicalSourceAtomically()
    {
        var setup = Tables +
            "CREATE VIEW app.v (public_id) AS SELECT s.id FROM app.src AS s;" +
            "CREATE VIEW app.child AS SELECT v.public_id FROM app.v AS v;" +
            "CREATE OR REPLACE VIEW app.v (public_id, label) " +
            "AS SELECT a.id, a.label FROM app.alt AS a;";
        var result = Analyze(setup);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Equal(SqlStatementEffectOperation.ReplaceView,
            Assert.Single(result.Statements[4].DetailedEffects).Operation);
        var view = result.FinalCatalog.Views.Single(v => v.Name == "app.v");
        Assert.Equal(new[] { "public_id", "label" }, view.Columns.Select(c => c.Name));
        Assert.Equal("app.alt", Assert.Single(view.Dependencies)!.DisplayName);
        Assert.Equal(new[] { "id", "label" }, view.SourceColumns);
        Assert.Equal("app.v", Assert.Single(result.FinalCatalog.Views
            .Single(v => v.Name == "app.child").Dependencies)!.DisplayName);

        var former = Analyze(setup + "ALTER TABLE app.src DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, former.Statements[5].Status);
        var current = Analyze(setup + "ALTER TABLE app.alt DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, current.Statements[5].Status);
        Assert.Empty(current.Statements[5].Changes);
    }

    [Theory]
    [InlineData("SELECT other.id FROM app.src AS s")]
    [InlineData("SELECT src.id FROM app.src AS s")]
    [InlineData("SELECT s.id FROM app.src AS other")]
    [InlineData("SELECT other.* FROM app.src AS s")]
    [InlineData("SELECT src.* FROM app.src AS s")]
    [InlineData("SELECT s.id, other.label FROM app.src s")]
    public void UnboundOrShadowedQualifierCannotMintKnownCatalog(string select)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + select + ";");
        var stmt = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, stmt.Status);
        Assert.Empty(stmt.Changes);
        Assert.Same(stmt.CatalogBefore, stmt.CatalogAfter);
        Assert.Empty(result.FinalCatalog.Views);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("SELECT s.id FROM app.src AS s WHERE s.id IN (1, 2)")]
    [InlineData("SELECT s.id FROM app.src s FULL JOIN app.alt a USING (id)")]
    [InlineData("SELECT s.id + 1 FROM app.src AS s")]
    [InlineData("SELECT app.src.id FROM app.src")]
    [InlineData("SELECT s.id FROM app.src AS s(x)")]
    [InlineData("SELECT s.id FROM app.src AS s, app.alt")]
    public void UnmodeledQualifiedGrammarRemainsUnsupported(string select)
    {
        var result = Analyze(Tables + "CREATE VIEW app.v AS " + select + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnknownCatalogDoesNotEstablishQualifierBinding()
    {
        var result = Analyze("CREATE VIEW app.v AS " +
            "SELECT s.id FROM app.src AS s;", SqlCatalogState.Unknown());
        var stmt = Assert.Single(result.Statements);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, stmt.Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonPostgresDialectsCannotInheritSingleSourceViewProof(
        SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT s.id FROM app.src AS s;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }
}
