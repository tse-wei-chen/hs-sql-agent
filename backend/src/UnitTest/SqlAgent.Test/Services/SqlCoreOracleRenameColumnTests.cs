using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Oracle ALTER TABLE RENAME COLUMN must rewrite proven physical-column
/// references in immutable Catalog snapshots, never string-replace SQL.
/// </summary>
public sealed class SqlCoreOracleRenameColumnTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType source = SqlAgentToolType.Oracle) =>
        SqlCoreFacade.AnalyzeScript(sql, source,
            catalog ?? SqlCatalogState.KnownEmpty(), null,
            TestContext.Current.CancellationToken);

    [Fact]
    public void RenamePhysicalColumnPreservesPositionTypeNullabilityAndOldSnapshot()
    {
        var result = Analyze(
            "CREATE TABLE orders (id NUMBER NOT NULL, note VARCHAR2(32));" +
            "ALTER TABLE orders RENAME COLUMN note TO description;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, x =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        Assert.Equal("RenameColumn",
            Assert.Single(result.Statements[1].Changes).Operation.ToString());
        var oldTable = Assert.Single(result.Statements[1].CatalogBefore.Tables);
        var next = Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(new[] { "ID", "NOTE" }, oldTable.Columns.Select(x => x.Name));
        Assert.Equal(new[] { "ID", "DESCRIPTION" }, next.Columns.Select(x => x.Name));
        Assert.Equal(oldTable.Columns[1].TypeName, next.Columns[1].TypeName);
        Assert.Equal(oldTable.Columns[1].Nullable, next.Columns[1].Nullable);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void RenameUpdatesMultiColumnStandaloneIndexesInPlace()
    {
        var result = Analyze(
            "CREATE TABLE orders (id NUMBER, note VARCHAR2(32));" +
            "CREATE INDEX ix_order ON orders(id, note);" +
            "ALTER TABLE orders RENAME COLUMN note TO details;" +
            "ALTER TABLE orders DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(new[] { "ID", "DETAILS" },
            Assert.Single(Assert.Single(result.Statements[2].CatalogAfter.Tables).Indexes).Columns);
        Assert.Equal(new[] { "ID", "NOTE" },
            Assert.Single(Assert.Single(result.Statements[1].CatalogAfter.Tables).Indexes).Columns);
    }

    [Fact]
    public void RenameUpdatesPrimaryKeyAndUniqueConstraintColumns()
    {
        var result = Analyze(
            "CREATE TABLE products (id NUMBER NOT NULL, code VARCHAR2(32), " +
            "CONSTRAINT pk_products PRIMARY KEY(id), CONSTRAINT uq_products UNIQUE(code));" +
            "ALTER TABLE products RENAME COLUMN code TO sku;");
        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(new[] { "SKU" },
            table.Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.Unique).Columns);
        Assert.Equal(new[] { "ID" },
            table.Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.PrimaryKey).Columns);
    }

    [Fact]
    public void RenameLocalForeignKeyColumnDoesNotRewriteReferencedTable()
    {
        var result = Analyze(
            "CREATE TABLE parent (id NUMBER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child (id NUMBER, parent_id NUMBER, " +
            "CONSTRAINT fk_child FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE child RENAME COLUMN parent_id TO parent_ref;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var fk = Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "CHILD").Constraints);
        Assert.Equal("PARENT_REF", Assert.Single(fk.Columns));
        Assert.Equal("ID", Assert.Single(fk.ReferencedColumns));
        Assert.Equal("ID",
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "PARENT").Columns).Name);
    }

    [Fact]
    public void InboundForeignKeyRewritesExactReferencedColumnLineage()
    {
        var result = Analyze(
            "CREATE TABLE parent (id NUMBER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child (id NUMBER, parent_id NUMBER, " +
            "CONSTRAINT fk_child FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE parent RENAME COLUMN id TO code;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogOperation.RenameColumn, Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal("ID", Assert.Single(result.Statements[2].CatalogBefore.Tables
            .Single(t => t.Name == "CHILD").Constraints.Single().ReferencedColumns));
        Assert.Equal("CODE", Assert.Single(result.FinalCatalog.Tables
            .Single(t => t.Name == "CHILD").Constraints.Single().ReferencedColumns));
        Assert.Equal("PARENT_ID", Assert.Single(result.FinalCatalog.Tables
            .Single(t => t.Name == "CHILD").Constraints.Single().Columns));
    }

    [Fact]
    public void RenameThenDropOldColumnMustFailAndNewColumnMustSucceed()
    {
        var result = Analyze(
            "CREATE TABLE logs (id NUMBER, note VARCHAR2(32));" +
            "ALTER TABLE logs RENAME COLUMN note TO details;" +
            "ALTER TABLE logs DROP COLUMN note;" +
            "ALTER TABLE logs DROP COLUMN details;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal("ID", Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns).Name);
    }

    [Theory]
    [InlineData("ALTER TABLE orders RENAME COLUMN missing TO other;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO id;")]
    [InlineData("ALTER TABLE missing RENAME COLUMN note TO other;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO note;")]
    public void InvalidRenamesDoNotPublishPartialMutations(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id NUMBER, note VARCHAR2(32));" + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Same(result.Statements[1].CatalogBefore, result.Statements[1].CatalogAfter);
    }

    [Theory]
    [InlineData("ALTER TABLE orders RENAME note TO details;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO details, DROP COLUMN id;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO details CASCADE;")]
    [InlineData("ALTER TABLE orders RENAME COLUMN note TO details ONLINE;")]
    public void UnsupportedRenameFormsNeverMintKnownDelta(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id NUMBER, note VARCHAR2(32));" + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void OpenWorldCatalogCannotProveRename()
    {
        var result = Analyze("ALTER TABLE orders RENAME COLUMN note TO details;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsDoNotInheritOracleRenameColumnGrammar(SqlAgentToolType source)
    {
        var result = Analyze("ALTER TABLE orders RENAME COLUMN note TO details;", source: source);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
    }
}
