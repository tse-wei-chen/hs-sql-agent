using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Inbound PostgreSQL FKs must follow physical referenced-column renames
/// atomically in immutable Virtual Catalog snapshots. No SQL is executed.
/// </summary>
public sealed class SqlCorePostgresInboundForeignKeyRenameTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres,
        SqlCatalogState? catalog = null) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect,
            catalog ?? SqlCatalogState.KnownEmpty(), null,
            TestContext.Current.CancellationToken);

    [Fact]
    public void IncomingForeignKeyTracksRenamedParentWhileLocalBindingIsUnchanged()
    {
        var result = Analyze(
            "CREATE TABLE parent (id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (parent_id INTEGER, CONSTRAINT fk_child " +
            "FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO parent_key;");

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Equal(SqlCatalogOperation.RenameColumn,
            Assert.Single(result.Statements[2].Changes).Operation);

        var originalParent = result.Statements[2].CatalogBefore.Tables.Single(t => t.Name == "parent");
        var originalChild = result.Statements[2].CatalogBefore.Tables.Single(t => t.Name == "child");
        var parent = result.FinalCatalog.Tables.Single(t => t.Name == "parent");
        var child = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("id", Assert.Single(originalParent.Columns).Name);
        Assert.Equal("parent_key", Assert.Single(parent.Columns).Name);
        Assert.Equal("id", Assert.Single(originalChild.Constraints).ReferencedColumns[0]);
        var fk = Assert.Single(child.Constraints);
        Assert.Equal(SqlCatalogConstraintKind.ForeignKey, fk.Kind);
        Assert.Equal("parent_id", Assert.Single(fk.Columns));
        Assert.Equal("parent_key", Assert.Single(fk.ReferencedColumns));
        Assert.Equal("fk_child", fk.Name);
        Assert.Equal(parent.QualifiedName.Key, fk.ReferencedIdentity.Key);
        Assert.NotSame(originalChild, child);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void TwoInboundTablesCompositeKeyAndRepeatedRenamePreserveExactPositions()
    {
        var result = Analyze(
            "CREATE TABLE parent (a INTEGER NOT NULL, b INTEGER NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(a,b));" +
            "CREATE TABLE first_child (fa INTEGER, fb INTEGER, " +
            "CONSTRAINT fk_first FOREIGN KEY(fa,fb) REFERENCES parent(a,b));" +
            "CREATE TABLE second_child (ga INTEGER, gb INTEGER, " +
            "CONSTRAINT fk_second FOREIGN KEY(ga,gb) REFERENCES parent(a,b));" +
            "ALTER TABLE parent RENAME COLUMN b TO b2;" +
            "ALTER TABLE parent RENAME COLUMN b2 TO final_b;");

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Equal(new[] { "a", "b" },
            result.Statements[3].CatalogBefore.Tables.Single(t => t.Name == "parent")
                .Constraints.Single().Columns);
        foreach (var child in result.Statements[3].CatalogAfter.Tables
                     .Where(t => t.Name != "parent"))
            Assert.Equal(new[] { "a", "b2" },
                Assert.Single(child.Constraints).ReferencedColumns);
        foreach (var child in result.FinalCatalog.Tables.Where(t => t.Name != "parent"))
            Assert.Equal(new[] { "a", "final_b" },
                Assert.Single(child.Constraints).ReferencedColumns);
        Assert.Equal(new[] { "a", "final_b" },
            result.FinalCatalog.Tables.Single(t => t.Name == "parent")
                .Constraints.Single().Columns);
        Assert.Equal(new[] { "fa", "fb" },
            result.FinalCatalog.Tables.Single(t => t.Name == "first_child")
                .Constraints.Single().Columns);
        Assert.Equal(new[] { "ga", "gb" },
            result.FinalCatalog.Tables.Single(t => t.Name == "second_child")
                .Constraints.Single().Columns);
    }

    [Fact]
    public void SelfReferencingForeignKeyRewritesBothReferencedAndLocalColumns()
    {
        var result = Analyze(
            "CREATE TABLE tree (id INTEGER NOT NULL, parent_id INTEGER, " +
            "CONSTRAINT pk_tree PRIMARY KEY(id), " +
            "CONSTRAINT fk_tree FOREIGN KEY(parent_id) REFERENCES tree(id));" +
            "ALTER TABLE tree RENAME COLUMN id TO node_id;" +
            "ALTER TABLE tree RENAME COLUMN parent_id TO ancestor_id;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var before = Assert.Single(result.Statements[1].CatalogBefore.Tables);
        Assert.Equal("id", before.Constraints.Single(c =>
            c.Kind == SqlCatalogConstraintKind.ForeignKey).ReferencedColumns[0]);
        var after = Assert.Single(result.FinalCatalog.Tables);
        var fk = after.Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.ForeignKey);
        Assert.Equal("node_id", Assert.Single(fk.ReferencedColumns));
        Assert.Equal("ancestor_id", Assert.Single(fk.Columns));
        Assert.Equal("node_id", Assert.Single(after.Constraints.Single(c =>
            c.Kind == SqlCatalogConstraintKind.PrimaryKey).Columns));
    }

    [Fact]
    public void UnrelatedForeignKeyToDifferentParentIsNotRetargeted()
    {
        var result = Analyze(
            "CREATE TABLE parent (id INTEGER NOT NULL, CONSTRAINT pk_p PRIMARY KEY(id));" +
            "CREATE TABLE other (id INTEGER NOT NULL, CONSTRAINT pk_o PRIMARY KEY(id));" +
            "CREATE TABLE child (one_id INTEGER, two_id INTEGER, " +
            "CONSTRAINT fk_p FOREIGN KEY(one_id) REFERENCES parent(id), " +
            "CONSTRAINT fk_o FOREIGN KEY(two_id) REFERENCES other(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO renamed_id;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var child = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("renamed_id", Assert.Single(child.Constraints.Single(c =>
            c.Name == "fk_p").ReferencedColumns));
        Assert.Equal("id", Assert.Single(child.Constraints.Single(c =>
            c.Name == "fk_o").ReferencedColumns));
        Assert.Equal("id", Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "other").Columns).Name);
    }

    [Fact]
    public void DownstreamDropStillRecognizesRenamedInboundReference()
    {
        var result = Analyze(
            "CREATE TABLE parent (id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (parent_id INTEGER, CONSTRAINT fk_child " +
            "FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO id2;" +
            "ALTER TABLE parent DROP COLUMN id2;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Equal("id2", Assert.Single(result.FinalCatalog.Tables.Single(
            t => t.Name == "parent").Columns).Name);
    }

    [Fact]
    public void OracleInboundForeignKeysStillFailClosed()
    {
        var result = Analyze(
            "CREATE TABLE parent (id NUMBER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (pid NUMBER, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO newid;",
            SqlAgentToolType.Oracle);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnknownCatalogCannotAssertInboundDependencies()
    {
        var result = Analyze("ALTER TABLE parent RENAME COLUMN id TO newid;",
            catalog: SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ExistingViewDependencyRemainsFailClosedEvenWithForeignKey()
    {
        var result = Analyze(
            "CREATE TABLE public.parent (id INTEGER NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE public.child (pid INTEGER, " +
            "CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES public.parent(id));" +
            "CREATE VIEW public.v AS SELECT id FROM public.parent;" +
            "ALTER TABLE public.parent RENAME COLUMN id TO newid;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }
}
