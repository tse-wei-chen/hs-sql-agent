using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Physical SQL identifier parts, not the lossy dotted display string, must
/// determine Catalog identity, DQL/DML name binding and DDL dependencies.
/// </summary>
public sealed class SqlCoreStructuredQualifiedNameTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Fact]
    public void DottedQuotedNameAndQualifiedName_AreDistinctPhysicalTables()
    {
        var result = Analyze(
            "CREATE TABLE \"app.orders\"(id INTEGER);" +
            "CREATE TABLE app.orders(id INTEGER, note TEXT);" +
            "SELECT id FROM \"app.orders\";" +
            "SELECT note FROM app.orders;" +
            "SELECT note FROM \"app.orders\";" +
            "DROP TABLE \"app.orders\";" +
            "SELECT id FROM app.orders;");

        Assert.Equal(7, result.Statements.Count);
        Assert.Empty(result.Statements[0].Diagnostics);
        Assert.Empty(result.Statements[1].Diagnostics);
        var both = result.Statements[1].CatalogAfter.Tables;
        Assert.Equal(2, both.Count);
        Assert.All(both, table => Assert.Equal("app.orders", table.Name));
        Assert.Equal(2, both.Select(table => table.QualifiedName.Key).Distinct().Count());
        Assert.Equal(new[] { 1, 2 }, both.Select(t => t.QualifiedName.Parts.Count).OrderBy(n => n));
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[3].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[4].NameBinding);
        Assert.Contains(result.Statements[4].Diagnostics, d => d.Code == "SQL_SCHEMA_COLUMN_NOT_FOUND");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
        Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(2, result.FinalCatalog.Tables[0].QualifiedName.Parts.Count);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[6].CatalogBinding);
    }

    [Fact]
    public void ExactStructuredForeignKey_PreventsOnlyTheActualParentDrop()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE \"app.parent\"(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(parent_id INTEGER, " +
            "FOREIGN KEY(parent_id) REFERENCES app.parent(id));" +
            "DROP TABLE \"app.parent\";" +
            "DROP TABLE app.parent;" +
            "DROP TABLE child;" +
            "DROP TABLE app.parent;");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var key = Assert.Single(result.Statements[2].CatalogAfter.Tables
            .Single(t => t.Name == "child").Constraints);
        Assert.Equal(2, key.ReferencedIdentity.Parts.Count);
        Assert.Equal("app.parent", key.ReferencedTable);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[4].Status);
        Assert.Empty(result.Statements[4].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[6].Status);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void QualifiedIndexesCannotBeConfusedWithQuotedDottedOwners()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER, note TEXT);" +
            "CREATE TABLE \"app.users\"(id INTEGER, note TEXT);" +
            "CREATE INDEX idx_real ON app.users(id);" +
            "ALTER TABLE \"app.users\" DROP COLUMN id;" +
            "ALTER TABLE app.users DROP COLUMN id;" +
            "DROP INDEX idx_real;" +
            "ALTER TABLE app.users DROP COLUMN id;");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[4].Status);
        Assert.Empty(result.Statements[4].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[6].Status);
        Assert.All(result.FinalCatalog.Tables, t => Assert.Single(t.Columns));
    }

    [Fact]
    public void RollbackRestoresTwoSameDisplayNamesAsDistinctCatalogEntries()
    {
        var result = Analyze(
            "CREATE TABLE \"app.users\"(id INTEGER);" +
            "BEGIN;" +
            "CREATE TABLE app.users(id INTEGER);" +
            "ROLLBACK;" +
            "SELECT id FROM \"app.users\";");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var snapshot = Assert.Single(result.Statements[3].CatalogAfter.Tables);
        Assert.Single(snapshot.QualifiedName.Parts);
        Assert.Equal("app.users", snapshot.QualifiedName.Parts[0]);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[4].CatalogBinding);
        Assert.Single(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "public", "users", "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "APP", "USERS", "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "APP", "USERS", "INTEGER")]
    public void NativeQuotedAndQualifiedNamesKeepDistinctParts(
        SqlAgentToolType dialect, string schema, string table, string typeName)
    {
        var sql = $"CREATE TABLE \"{schema}.{table}\"(id {typeName});" +
                  $"CREATE TABLE {schema}.{table}(id {typeName});";
        var result = SqlCoreFacade.AnalyzeScript(
            sql, dialect, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.FinalCatalog.Tables.Count);
        Assert.Contains(result.FinalCatalog.Tables, t => t.QualifiedName.Parts.Count == 1);
        Assert.Contains(result.FinalCatalog.Tables, t => t.QualifiedName.Parts.Count == 2);
    }

    [Fact]
    public void StructuredPartDisplayIsNotANamespaceLookupKey()
    {
        var result = Analyze(
            "CREATE TABLE \"x.y\"(id INTEGER);" +
            "DROP TABLE x.y;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(SqlCatalogOperation.CreateTable,
            Assert.Single(result.Statements[0].Changes).Operation);
        Assert.Equal("x.y", result.FinalCatalog.Tables[0].Name);
        Assert.Single(result.FinalCatalog.Tables[0].QualifiedName.Parts);
    }
}
