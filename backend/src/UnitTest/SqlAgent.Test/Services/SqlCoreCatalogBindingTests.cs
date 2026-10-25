using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreCatalogBindingTests
{
    private static SqlScriptAnalysisResult AnalyzeKnown(string script) =>
        SqlCoreFacade.AnalyzeScript(script, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Fact]
    public void DdlSequence_ProvesColumnsBeforeDropAndRejectsReferencesAfterDrop()
    {
        var result = AnalyzeKnown(
            "CREATE TABLE users(id INTEGER, name TEXT);" +
            "SELECT id FROM users;" +
            "UPDATE users SET name = 'Ada' WHERE id = 1;" +
            "ALTER TABLE users DROP COLUMN name;" +
            "SELECT name FROM users;" +
            "DROP TABLE users;" +
            "SELECT id FROM users;");

        Assert.Equal(7, result.Statements.Count);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, result.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[4].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[4].Status);
        Assert.Contains(result.Statements[4].Diagnostics,
            d => d.Code == "SQL_SCHEMA_COLUMN_NOT_FOUND");
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[6].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[6].Status);
        Assert.Contains(result.Statements[6].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
        Assert.Empty(result.FinalCatalog.Tables);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void TypedCatalogRejectsMismatchForUpdateAndInsert()
    {
        var result = AnalyzeKnown(
            "CREATE TABLE users(id INTEGER, name TEXT);" +
            "UPDATE users SET id = 'not a number' WHERE id = 1;" +
            "INSERT INTO users(id, name) VALUES ('not a number', 'Ada');" +
            "UPDATE users SET name = 'Ada' WHERE id = 1;");

        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[1].CatalogBinding);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[2].CatalogBinding);
        Assert.Contains(result.Statements[2].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[3].CatalogBinding);
    }

    [Fact]
    public void QueryWithoutPhysicalTablesCanBindAgainstKnownEmptyCatalog()
    {
        var result = AnalyzeKnown("SELECT 1;");
        Assert.Equal(SqlCatalogBindingStatus.Verified, Assert.Single(result.Statements).CatalogBinding);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void DefaultOpenWorldNeverClaimsProvenNameOrTypeBinding()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SELECT id FROM users", SqlAgentToolType.Postgres);
        var stmt = Assert.Single(result.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, stmt.Certainty);
        Assert.Contains(stmt.Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
    }

    [Fact]
    public void UnsupportedDdlTaintsPreviouslyVerifiedCatalogAndLaterBinding()
    {
        var result = AnalyzeKnown(
            "CREATE TABLE users(id INTEGER);" +
            "SELECT id FROM users;" +
            "ALTER TABLE users ADD COLUMN name TEXT DEFAULT 'A';" +
            "SELECT id FROM users;");

        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[2].Status);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[3].CatalogBinding);
        Assert.Contains(result.Statements[3].Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnknownDDLConditionalCreateDoesNotInventProofs()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE IF NOT EXISTS users(id INTEGER);" +
            "SELECT id FROM users;",
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[1].Certainty);
    }

    [Fact]
    public void OtherDialectDoesNotConsumePostgresqlOnlyStaticCatalogProof()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SELECT 1", SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());
        var stmt = Assert.Single(result.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Contains(stmt.Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_DIALECT_UNPROVEN");
    }

    [Fact]
    public void CatalogBindingCannotGrantSqlExecutionOrCompileEvidence()
    {
        var result = AnalyzeKnown("CREATE TABLE users(id INTEGER); SELECT id FROM users;");
        var stmt = result.Statements[1];
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.DoesNotContain(typeof(SqlScriptAnalysisResult).GetProperties(),
            p => p.Name.Contains("Command", StringComparison.Ordinal) ||
                 p.Name.Contains("Evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SqlStatementAnalysis).GetProperties(),
            p => p.Name.Contains("Command", StringComparison.Ordinal) ||
                 p.Name.Contains("Evidence", StringComparison.Ordinal));
    }
}
