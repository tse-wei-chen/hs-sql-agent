using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreMultiDialectCatalogBindingTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "VARCHAR(32)")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "VARCHAR2(32)")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER", "VARCHAR(32)")]
    public void ModelledDialectTypes_AreValidatedAgainstPriorDdl(
        SqlAgentToolType dialect, string numberType, string textType)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users (id {numberType}, name {textType});" +
            "SELECT name FROM users;" +
            "UPDATE users SET name = 'Ada' WHERE id = 1;" +
            "UPDATE users SET id = 'not-an-integer' WHERE id = 1;" +
            (dialect == SqlAgentToolType.Firebird
                ? "ALTER TABLE users DROP name;"
                : "ALTER TABLE users DROP COLUMN name;") +
            "SELECT name FROM users;",
            dialect, SqlCatalogState.KnownEmpty());

        Assert.Equal(6, result.Statements.Count);
        Assert.Empty(result.Statements[0].Diagnostics);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].TypeBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[3].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[3].NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[3].TypeBinding);
        Assert.Contains(result.Statements[3].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[5].NameBinding);
        Assert.Contains(result.Statements[5].Diagnostics,
            d => d.Code == "SQL_SCHEMA_COLUMN_NOT_FOUND");
    }

    [Fact]
    public void SqliteKnowsNamesButNeverClaimsRigidColumnTypeProof()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users (id INTEGER, name TEXT);" +
            "SELECT id FROM users;" +
            "INSERT INTO users(id, name) VALUES ('text-is-legal', 'Ada');" +
            "ALTER TABLE users ADD COLUMN active INTEGER;" +
            "SELECT missing FROM users;",
            SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty());

        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].TypeBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[2].TypeBinding);
        Assert.DoesNotContain(result.Statements[2].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[4].NameBinding);
        Assert.Contains(result.Statements[4].Diagnostics,
            d => d.Code == "SQL_SCHEMA_COLUMN_NOT_FOUND");
        Assert.Equal(3, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "INT", "users")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT", "USERS")]
    public void ProviderCollationUnproven_CannotCertifyPhysicalTableBinding(
        SqlAgentToolType dialect, string numberType, string expectedTable)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users (id {numberType});" +
            "SELECT id FROM users;" +
            "SELECT 1;",
            dialect, SqlCatalogState.KnownEmpty());

        Assert.Equal(expectedTable, Assert.Single(result.Statements[0].CatalogAfter.Tables).Name);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].TypeBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_CATALOG_BINDING_IDENTIFIER_RULES_UNPROVEN");
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Empty(result.Statements[2].Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void QuotedIdentifiersStayCaseSensitiveAcrossDdlAndDql(
        SqlAgentToolType dialect, string typeName)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE \"Mixed\" (\"Id\" {typeName});" +
            "SELECT \"Id\" FROM \"Mixed\";" +
            "SELECT ID FROM \"Mixed\";" +
            "SELECT \"Id\" FROM MIXED;",
            dialect, SqlCatalogState.KnownEmpty());

        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[2].NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[3].NameBinding);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnknownWorldNeverGetsAccidentalCatalogProof(SqlAgentToolType dialect)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SELECT id FROM users", dialect);
        var statement = Assert.Single(result.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, statement.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, statement.TypeBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, statement.CatalogBinding);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void StaticProofDoesNotGrantExecutionAcrossAllSources(SqlAgentToolType dialect)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SELECT 1", dialect, SqlCatalogState.KnownEmpty());

        var item = Assert.Single(result.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, item.Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, item.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, item.TypeBinding);
        Assert.DoesNotContain(typeof(SqlStatementAnalysis).GetProperties(),
            property => property.Name.Contains("Evidence", StringComparison.Ordinal) ||
                        property.Name.Contains("Command", StringComparison.Ordinal));
    }
}
