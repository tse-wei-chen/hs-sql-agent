using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL DROP CONSTRAINT is one atomic dependency-graph transition.
/// No partial FK drop and no false Known facts on unresolved key identity.
/// </summary>
public sealed class SqlCorePostgresDropConstraintDependencyTests
{
    private static SqlScriptAnalysisResult Analyze(string script,
        SqlCatalogState? initial = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(script, dialect, initial ?? SqlCatalogState.KnownEmpty(),
            null, TestContext.Current.CancellationToken);

    private const string ParentChildren =
        "CREATE TABLE app.parent(id INTEGER NOT NULL, code INTEGER NOT NULL, " +
        "CONSTRAINT pk_parent PRIMARY KEY(id), CONSTRAINT uq_parent_code UNIQUE(code));" +
        "CREATE TABLE app.child_a(pid INTEGER, CONSTRAINT fk_a FOREIGN KEY(pid) REFERENCES app.parent(id));" +
        "CREATE TABLE app.child_b(pid INTEGER, CONSTRAINT fk_b FOREIGN KEY(pid) REFERENCES app.parent(id));" +
        "CREATE TABLE app.child_c(code INTEGER, CONSTRAINT fk_code FOREIGN KEY(code) REFERENCES app.parent(code));";

    [Fact]
    public void CascadeDropsOnlyForeignKeysProvenDependentOnSpecificParentConstraint()
    {
        var result = Analyze(ParentChildren +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent CASCADE;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        var step = result.Statements[4];
        Assert.Equal(SqlCatalogKnowledge.Known, step.Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, step.EffectCoverage);
        Assert.Equal(new[] { "app.parent", "app.child_a", "app.child_b" },
            step.Changes.Select(c => c.TableName).ToArray());
        Assert.All(step.Changes, c => Assert.Equal(SqlCatalogOperation.DropConstraint, c.Operation));
        Assert.Equal(3, step.DetailedEffects.Count);
        Assert.Equal(1, result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.parent").Constraints.Count);
        Assert.Equal("uq_parent_code", Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.parent").Constraints).Name);
        Assert.Empty(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.child_a").Constraints);
        Assert.Empty(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.child_b").Constraints);
        Assert.Equal("fk_code", Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.child_c").Constraints).Name);
        Assert.Equal(2, step.CatalogBefore.Tables.Single(t =>
            t.Name == "app.parent").Constraints.Count);
        Assert.Equal("fk_a", Assert.Single(step.CatalogBefore.Tables.Single(t =>
            t.Name == "app.child_a").Constraints).Name);
    }

    [Fact]
    public void RestrictAndDefaultRejectInboundForeignKeysWithoutMutatingCatalog()
    {
        foreach (var clause in new[] { "", " RESTRICT" })
        {
            var result = Analyze(ParentChildren +
                "ALTER TABLE app.parent DROP CONSTRAINT pk_parent" + clause + ";");
            var step = result.Statements[4];
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, step.Status);
            Assert.Empty(step.Changes);
            Assert.Same(step.CatalogBefore, step.CatalogAfter);
            Assert.Equal(4, result.FinalCatalog.Tables.Count);
        }
    }

    [Fact]
    public void CompositeSelfReferenceAndTransitiveGraphStayExactlyScoped()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(a INTEGER NOT NULL, b INTEGER NOT NULL, " +
            "CONSTRAINT pk_pair PRIMARY KEY(a,b));" +
            "CREATE TABLE app.child_a(x INTEGER, y INTEGER, CONSTRAINT fk_a " +
            "FOREIGN KEY(x,y) REFERENCES app.parent(a,b));" +
            "CREATE TABLE app.child_b(p INTEGER, q INTEGER, CONSTRAINT fk_b " +
            "FOREIGN KEY(p,q) REFERENCES app.parent(a,b));" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_pair CASCADE;" +
            "ALTER TABLE app.parent DROP COLUMN b;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        Assert.Equal(3, result.Statements[3].Changes.Count);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "app.child_a").Constraints);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "app.child_b").Constraints);
        Assert.Equal(new[] { "a" }, result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.parent").Columns.Select(c => c.Name));
    }

    [Fact]
    public void SelfReferencingForeignKeyDropsWithPrimaryKeyButRetainsOtherKey()
    {
        var result = Analyze(
            "CREATE TABLE app.tree(id INTEGER NOT NULL, parent_id INTEGER, name INTEGER NOT NULL, " +
            "CONSTRAINT pk_tree PRIMARY KEY(id), CONSTRAINT uq_name UNIQUE(name), " +
            "CONSTRAINT fk_tree FOREIGN KEY(parent_id) REFERENCES app.tree(id));" +
            "ALTER TABLE app.tree DROP CONSTRAINT pk_tree CASCADE;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal(2, result.Statements[1].Changes.Count);
        Assert.Equal("uq_name", Assert.Single(result.FinalCatalog.Tables.Single().Constraints).Name);
    }

    [Fact]
    public void DropForeignKeyCascadeDoesNotDeleteParentKey()
    {
        var result = Analyze(ParentChildren +
            "ALTER TABLE app.child_a DROP CONSTRAINT fk_a CASCADE;");
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Statements[4].Changes);
        Assert.Equal(2, result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.parent").Constraints.Count);
        Assert.Empty(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.child_a").Constraints);
        Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "app.child_b").Constraints);
    }

    [Theory]
    [InlineData("ALTER TABLE app.parent DROP CONSTRAINT IF EXISTS missing;")]
    [InlineData("ALTER TABLE app.parent DROP CONSTRAINT IF EXISTS missing RESTRICT;")]
    [InlineData("ALTER TABLE app.parent DROP CONSTRAINT IF EXISTS missing CASCADE;")]
    public void ConditionalMissingConstraintPublishesExactNoOp(string sql)
    {
        var result = Analyze(ParentChildren + sql);
        Assert.Empty(result.Diagnostics);
        var step = result.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.Equal(SqlEffectCoverage.Complete, step.EffectCoverage);
        Assert.Empty(step.Changes);
        Assert.Empty(step.DetailedEffects);
        Assert.Same(step.CatalogBefore, step.CatalogAfter);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MissingConstraintWithoutIfExistsIsInvalidAndAtomic()
    {
        var result = Analyze(ParentChildren +
            "ALTER TABLE app.parent DROP CONSTRAINT missing CASCADE;");
        var step = result.Statements[4];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, step.Status);
        Assert.Empty(step.Changes);
        Assert.Same(step.CatalogBefore, step.CatalogAfter);
    }

    [Fact]
    public void ConditionalMissingConstraintDoesNotHideMissingTable()
    {
        var result = Analyze(
            "ALTER TABLE app.missing DROP CONSTRAINT IF EXISTS unknown CASCADE;");
        var step = Assert.Single(result.Statements);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.Empty(step.Changes);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void CatalogUncertaintyNeverMintsCascadeProof()
    {
        var result = Analyze(
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent CASCADE;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AmbiguousDuplicateParentKeysCannotGuessWhichFkIndexToCascade()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(id), CONSTRAINT uq_same UNIQUE(id));" +
            "CREATE TABLE app.child(pid INTEGER, " +
            "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent CASCADE;");
        var step = result.Statements[2];
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.Empty(step.Changes.Where(c =>
            c.Certainty == SqlCatalogKnowledge.Known));
        Assert.NotEqual(SqlCatalogKnowledge.Known, step.Certainty);
    }

    [Fact]
    public void ViewOnParentTableSurvivesDroppingOnlyPrimaryKeyAndInboundForeignKeys()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.child(pid INTEGER, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "CREATE VIEW app.v AS SELECT id FROM app.parent;" +
            "ALTER TABLE app.parent DROP CONSTRAINT pk_parent CASCADE;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal("app.parent", Assert.Single(result.FinalCatalog.Views.Single().Dependencies)!.DisplayName);
        Assert.Equal("id", Assert.Single(result.FinalCatalog.Views.Single().Columns).Name);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "app.parent").Constraints);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "app.child").Constraints);
    }

    [Theory]
    [InlineData("ALTER TABLE app.parent DROP CONSTRAINT pk_parent, DROP COLUMN id;")]
    [InlineData("ALTER TABLE app.parent DROP CONSTRAINT pk_parent CASCADE, DROP COLUMN id;")]
    [InlineData("ALTER TABLE app.parent DROP CONSTRAINT pk_parent RESTRICT CASCADE;")]
    public void CompoundOrConflictingSyntaxRemainsUnsupported(string command)
    {
        var result = Analyze(ParentChildren + command);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.Statements[4].Changes.Where(c =>
            c.Certainty == SqlCatalogKnowledge.Known));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void OtherDialectsDoNotInheritPostgresqlDropConstraintCascade(SqlAgentToolType dialect)
    {
        var result = Analyze("ALTER TABLE app.t DROP CONSTRAINT key CASCADE;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
    }
}
