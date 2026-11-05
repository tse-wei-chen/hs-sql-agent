using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Oracle inbound/self-referencing FK lineage must be rewritten from
/// one immutable pre-rename Catalog, without promoting unproven Views.</summary>
public sealed class SqlCoreOracleInboundForeignKeyRenameTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Oracle) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect,
            catalog ?? SqlCatalogState.KnownEmpty(), null,
            TestContext.Current.CancellationToken);

    [Fact]
    public void InboundForeignKeyTracksParentRenameWithoutChangingLocalBinding()
    {
        var result = Analyze(
            "CREATE TABLE parent (id NUMBER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (parent_id NUMBER, CONSTRAINT fk_child " +
            "FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO parent_key;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(SqlCatalogOperation.RenameColumn, Assert.Single(result.Statements[2].Changes).Operation);
        var before = result.Statements[2].CatalogBefore;
        Assert.Equal("ID", Assert.Single(before.Tables.Single(t => t.Name == "PARENT").Columns).Name);
        Assert.Equal("ID", Assert.Single(before.Tables.Single(t => t.Name == "CHILD")
            .Constraints.Single().ReferencedColumns));
        var parent = result.FinalCatalog.Tables.Single(t => t.Name == "PARENT");
        var child = result.FinalCatalog.Tables.Single(t => t.Name == "CHILD");
        var fk = Assert.Single(child.Constraints);
        Assert.Equal("PARENT_KEY", Assert.Single(parent.Columns).Name);
        Assert.Equal("PARENT_KEY", Assert.Single(fk.ReferencedColumns));
        Assert.Equal("PARENT_ID", Assert.Single(fk.Columns));
        Assert.Equal("FK_CHILD", fk.Name);
        Assert.Equal(parent.QualifiedName!.Key, fk.ReferencedIdentity!.Key);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.NotSame(before.Tables.Single(t => t.Name == "CHILD"), child);
    }

    [Fact]
    public void CompositeKeyAndMultipleChildTablesFollowRepeatedRename()
    {
        var result = Analyze(
            "CREATE TABLE parent (a NUMBER NOT NULL, b NUMBER NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(a,b));" +
            "CREATE TABLE child_a (x NUMBER, y NUMBER, " +
            "CONSTRAINT fk_a FOREIGN KEY(x,y) REFERENCES parent(a,b));" +
            "CREATE TABLE child_b (m NUMBER, n NUMBER, " +
            "CONSTRAINT fk_b FOREIGN KEY(m,n) REFERENCES parent(a,b));" +
            "ALTER TABLE parent RENAME COLUMN b TO intermediate;" +
            "ALTER TABLE parent RENAME COLUMN intermediate TO new_b;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(new[] { "A", "B" }, result.Statements[3].CatalogBefore.Tables
            .Single(t => t.Name == "CHILD_A").Constraints.Single().ReferencedColumns);
        foreach (var child in result.Statements[3].CatalogAfter.Tables.Where(t => t.Name != "PARENT"))
            Assert.Equal(new[] { "A", "INTERMEDIATE" }, Assert.Single(child.Constraints).ReferencedColumns);
        foreach (var child in result.FinalCatalog.Tables.Where(t => t.Name != "PARENT"))
            Assert.Equal(new[] { "A", "NEW_B" }, Assert.Single(child.Constraints).ReferencedColumns);
        Assert.Equal(new[] { "A", "NEW_B" }, result.FinalCatalog.Tables.Single(t =>
            t.Name == "PARENT").Constraints.Single().Columns);
        Assert.Equal(new[] { "X", "Y" }, result.FinalCatalog.Tables.Single(t =>
            t.Name == "CHILD_A").Constraints.Single().Columns);
    }

    [Fact]
    public void SelfReferencingConstraintRenamesBothLocalAndTargetIfNeeded()
    {
        var result = Analyze(
            "CREATE TABLE tree (id NUMBER NOT NULL, parent_id NUMBER, " +
            "CONSTRAINT pk_tree PRIMARY KEY(id), " +
            "CONSTRAINT fk_tree FOREIGN KEY(parent_id) REFERENCES tree(id));" +
            "ALTER TABLE tree RENAME COLUMN id TO node_id;" +
            "ALTER TABLE tree RENAME COLUMN parent_id TO ancestor_id;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var fk = result.FinalCatalog.Tables.Single().Constraints.Single(c =>
            c.Kind == SqlCatalogConstraintKind.ForeignKey);
        Assert.Equal("NODE_ID", Assert.Single(fk.ReferencedColumns));
        Assert.Equal("ANCESTOR_ID", Assert.Single(fk.Columns));
        Assert.Equal("ID", result.Statements[1].CatalogBefore.Tables.Single().Constraints
            .Single(c => c.Kind == SqlCatalogConstraintKind.ForeignKey).ReferencedColumns[0]);
    }

    [Fact]
    public void UnrelatedForeignKeyTargetNeverChanges()
    {
        var result = Analyze(
            "CREATE TABLE first_parent (id NUMBER NOT NULL, CONSTRAINT pk_first PRIMARY KEY(id));" +
            "CREATE TABLE other_parent (id NUMBER NOT NULL, CONSTRAINT pk_other PRIMARY KEY(id));" +
            "CREATE TABLE child (first_id NUMBER, other_id NUMBER, " +
            "CONSTRAINT fk_first FOREIGN KEY(first_id) REFERENCES first_parent(id), " +
            "CONSTRAINT fk_other FOREIGN KEY(other_id) REFERENCES other_parent(id));" +
            "ALTER TABLE first_parent RENAME COLUMN id TO code;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("CODE", Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "CHILD").Constraints.Single(c => c.Name == "FK_FIRST").ReferencedColumns));
        Assert.Equal("ID", Assert.Single(result.FinalCatalog.Tables.Single(t =>
            t.Name == "CHILD").Constraints.Single(c => c.Name == "FK_OTHER").ReferencedColumns));
    }

    [Fact]
    public void DownstreamRestrictedDropStillProtectsRenamedInboundKey()
    {
        var result = Analyze(
            "CREATE TABLE parent (id NUMBER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (pid NUMBER, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO new_id;" +
            "ALTER TABLE parent DROP COLUMN new_id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Equal("NEW_ID", result.FinalCatalog.Tables.Single(t =>
            t.Name == "PARENT").Columns.Single().Name);
    }

    [Fact]
    public void DependentViewRemainsUnprovenEvenAlongsideInboundFk()
    {
        // Oracle invalidates dependent Views on column rename; unlike
        // PostgreSQL, preserving the old output schema is not proven.
        var result = Analyze(
            "CREATE TABLE parent (id NUMBER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (pid NUMBER, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));" +
            "CREATE VIEW v AS SELECT id FROM parent;" +
            "ALTER TABLE parent RENAME COLUMN id TO new_id;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnknownCatalogNeverProvesInboundFkAbsence()
    {
        var result = Analyze("ALTER TABLE parent RENAME COLUMN id TO code;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements.Single().Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("ALTER TABLE parent RENAME COLUMN absent TO code;")]
    [InlineData("ALTER TABLE parent RENAME COLUMN id TO note;")]
    [InlineData("ALTER TABLE parent RENAME COLUMN id TO id;")]
    public void InvalidRenameNeverPublishesKnownCrossTableChange(string alter)
    {
        var result = Analyze(
            "CREATE TABLE parent (id NUMBER NOT NULL, note NUMBER, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child (pid NUMBER, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));" +
            alter);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Same(result.Statements[2].CatalogBefore, result.Statements[2].CatalogAfter);
    }

    [Fact]
    public void OracleRenameStillRequiresColumnKeyword()
    {
        var result = Analyze("CREATE TABLE parent (id NUMBER);" +
            "ALTER TABLE parent RENAME id TO next_id;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
    }
}
