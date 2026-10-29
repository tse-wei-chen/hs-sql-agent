using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL RENAME [COLUMN] must preserve an immutable, typed Catalog
/// lineage without claiming dependent objects it cannot rewrite.
/// </summary>
public sealed class SqlCorePostgresRenameColumnTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect,
            catalog ?? SqlCatalogState.KnownEmpty(), null,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO details;")]
    [InlineData("ALTER TABLE orders RENAME note TO details;")]
    public void BothPostgresRenameSpellingsPreserveOrderTypeNullabilityAndOldSnapshot(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id INTEGER NOT NULL, note TEXT);" + ddl);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(SqlCatalogOperation.RenameColumn,
            Assert.Single(result.Statements[1].Changes).Operation);
        var before = Assert.Single(result.Statements[1].CatalogBefore.Tables);
        var after = Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(new[] { "id", "note" }, before.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "details" }, after.Columns.Select(c => c.Name));
        Assert.Equal(before.Columns[1].TypeName, after.Columns[1].TypeName);
        Assert.Equal(before.Columns[1].Nullable, after.Columns[1].Nullable);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void RenameRewritesStandaloneIndexColumnsAndLeavesPastSnapshotUntouched()
    {
        var result = Analyze(
            "CREATE TABLE orders (id INTEGER, note TEXT);" +
            "CREATE INDEX ix_orders_note ON orders(id, note);" +
            "ALTER TABLE orders RENAME COLUMN note TO details;" +
            "ALTER TABLE orders DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(new[] { "id", "note" },
            Assert.Single(Assert.Single(result.Statements[1].CatalogAfter.Tables).Indexes).Columns);
        Assert.Equal(new[] { "id", "details" },
            Assert.Single(Assert.Single(result.Statements[2].CatalogAfter.Tables).Indexes).Columns);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
    }

    [Fact]
    public void LocalPrimaryUniqueAndForeignKeyReferencesFollowRename()
    {
        var result = Analyze(
            "CREATE TABLE parents (id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE children (id INTEGER, parent_id INTEGER, code TEXT, " +
            "CONSTRAINT fk_child FOREIGN KEY(parent_id) REFERENCES parents(id), " +
            "CONSTRAINT uq_child UNIQUE(code));" +
            "ALTER TABLE children RENAME COLUMN parent_id TO parent_ref;" +
            "ALTER TABLE children RENAME COLUMN code TO sku;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var child = result.FinalCatalog.Tables.Single(t => t.Name == "children");
        var fk = child.Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.ForeignKey);
        Assert.Equal("parent_ref", Assert.Single(fk.Columns));
        Assert.Equal("id", Assert.Single(fk.ReferencedColumns));
        Assert.Equal("sku", Assert.Single(
            child.Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.Unique).Columns));
    }

    [Fact]
    public void InboundForeignKeyAndDependentViewRemainUnproven()
    {
        var inbound = Analyze(
            "CREATE TABLE parents (id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE children (parent_id INTEGER, CONSTRAINT fk_child " +
            "FOREIGN KEY(parent_id) REFERENCES parents(id));" +
            "ALTER TABLE parents RENAME COLUMN id TO new_id;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, inbound.Statements[2].Status);
        Assert.DoesNotContain(inbound.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);

        var view = Analyze(
            "CREATE TABLE public.items (id INTEGER, name TEXT);" +
            "CREATE VIEW public.item_names AS SELECT name FROM public.items;" +
            "ALTER TABLE public.items RENAME COLUMN name TO label;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, view.Statements[1].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, view.Statements[2].Status);
        Assert.DoesNotContain(view.Statements[2].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("ALTER TABLE orders RENAME COLUMN missing TO details;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO id;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO note;")]
    [InlineData("ALTER TABLE missing RENAME COLUMN note TO details;")]
    public void InvalidRenamesNeverPublishAnAtomicKnownDelta(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id INTEGER, note TEXT);" + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO details, DROP COLUMN id;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO details CASCADE;")]
    [InlineData("ALTER TABLE orders RENAME TO new_orders;")]
    public void UnsupportedCompoundOrTableRenameDoesNotClaimColumnProof(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id INTEGER, note TEXT);" + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void OpenWorldCannotAuthorizeRename()
    {
        var result = Analyze("ALTER TABLE orders RENAME COLUMN note TO details;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherUnmodeledDialectsDoNotInheritPostgresRename(SqlAgentToolType dialect)
    {
        var result = Analyze("ALTER TABLE orders RENAME COLUMN note TO details;",
            SqlCatalogState.KnownEmpty(), dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.DoesNotContain(result.CatalogChanges,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }
}
