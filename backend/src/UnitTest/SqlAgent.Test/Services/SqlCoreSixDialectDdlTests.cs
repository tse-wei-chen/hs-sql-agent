using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Six-source TABLE DDL capability matrix. Catalog effects are static, never
/// SQL commands. Unsupported dialect syntax invalidates the catalog proof.
/// </summary>
public sealed class SqlCoreSixDialectDdlTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres,
        "CREATE TABLE users (id INTEGER, name VARCHAR(32));",
        "ALTER TABLE users ADD COLUMN active BOOLEAN;",
        "ALTER TABLE users DROP COLUMN name;",
        "DROP TABLE users;", "users", "active")]
    [InlineData(SqlAgentToolType.MySQL,
        "CREATE TABLE users (id INT, name VARCHAR(32));",
        "ALTER TABLE users ADD COLUMN active BOOLEAN;",
        "ALTER TABLE users DROP COLUMN name;",
        "DROP TABLE users;", "users", "active")]
    [InlineData(SqlAgentToolType.MsSqlServer,
        "CREATE TABLE users (id INT, name NVARCHAR(32));",
        "ALTER TABLE users ADD active BIT;",
        "ALTER TABLE users DROP COLUMN name;",
        "DROP TABLE users;", "USERS", "ACTIVE")]
    [InlineData(SqlAgentToolType.Sqlite,
        "CREATE TABLE users (id INTEGER, name TEXT);",
        "ALTER TABLE users ADD COLUMN active INTEGER;",
        "ALTER TABLE users DROP COLUMN name;",
        "DROP TABLE users;", "USERS", "ACTIVE")]
    [InlineData(SqlAgentToolType.Oracle,
        "CREATE TABLE users (id NUMBER, name VARCHAR2(32));",
        "ALTER TABLE users ADD (active NUMBER);",
        "ALTER TABLE users DROP COLUMN name;",
        "DROP TABLE users;", "USERS", "ACTIVE")]
    [InlineData(SqlAgentToolType.Firebird,
        "CREATE TABLE users (id INTEGER, name VARCHAR(32));",
        "ALTER TABLE users ADD active SMALLINT;",
        "ALTER TABLE users DROP name;",
        "DROP TABLE users;", "USERS", "ACTIVE")]
    public void SupportedNativeTableDdl_PreservesExactOrderedCatalogDeltas(
        SqlAgentToolType provider, string create, string add, string dropColumn, string dropTable,
        string tableName, string columnName)
    {
        // SQLite DROP COLUMN was added in 3.35; a versionless profile must
        // not be interpreted as proving that capability.
        var versionProof = provider == SqlAgentToolType.Sqlite
            ? new SqlProviderCapabilityProfile(provider, new Version(3, 45))
            : null;
        var result = SqlCoreFacade.AnalyzeScript(
            create + add + dropColumn + dropTable, provider,
            SqlCatalogState.KnownEmpty(), versionProof);

        Assert.Equal(4, result.Statements.Count);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
        {
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
            Assert.Equal(SqlCatalogKnowledge.Known, statement.Certainty);
            // Oracle DDL commits before/after the statement. It must also
            // report a potential transaction boundary; five other dialects
            // retain their previously modeled ChangeSchema-only shape.
            var expectedEffects = provider == SqlAgentToolType.Oracle
                ? SqlEffectKind.ChangeSchema | SqlEffectKind.ChangeTransaction
                : SqlEffectKind.ChangeSchema;
            Assert.Equal(expectedEffects, statement.Effects);
            Assert.Single(statement.Changes);
        });
        Assert.Equal(SqlCatalogOperation.CreateTable, result.Statements[0].Changes[0].Operation);
        Assert.Equal(SqlCatalogOperation.AddColumn, result.Statements[1].Changes[0].Operation);
        Assert.Equal(SqlCatalogOperation.DropColumn, result.Statements[2].Changes[0].Operation);
        Assert.Equal(SqlCatalogOperation.DropTable, result.Statements[3].Changes[0].Operation);

        Assert.Equal(tableName, Assert.Single(result.Statements[0].CatalogAfter.Tables).Name);
        var afterAdd = Assert.Single(result.Statements[1].CatalogAfter.Tables);
        Assert.Equal(3, afterAdd.Columns.Count);
        Assert.Equal(columnName, afterAdd.Columns[2].Name);
        Assert.Equal(2, Assert.Single(result.Statements[2].CatalogAfter.Tables).Columns.Count);
        Assert.Empty(result.FinalCatalog.Tables);
        Assert.Equal(SqlCatalogKnowledge.Known, result.FinalCatalog.Knowledge);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "users")]
    [InlineData(SqlAgentToolType.MySQL, "users")]
    [InlineData(SqlAgentToolType.Sqlite, "USERS")]
    public void IfNotExistsAndIfExists_AreExactNoOpsOnlyWhenProven(
        SqlAgentToolType provider, string tableName)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users (id INTEGER);" +
            "CREATE TABLE IF NOT EXISTS users (name TEXT);" +
            "DROP TABLE users;" +
            "DROP TABLE IF EXISTS users;",
            provider, SqlCatalogState.KnownEmpty());

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(tableName, result.Statements[0].Changes[0].TableName);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Equal(SqlCatalogKnowledge.Known, result.FinalCatalog.Knowledge);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "CREATE TABLE IF NOT EXISTS users (id INT)")]
    [InlineData(SqlAgentToolType.Oracle, "CREATE TABLE IF NOT EXISTS users (id NUMBER)")]
    [InlineData(SqlAgentToolType.Firebird, "CREATE TABLE IF NOT EXISTS users (id INTEGER)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "DROP TABLE IF EXISTS users")]
    [InlineData(SqlAgentToolType.Oracle, "DROP TABLE IF EXISTS users")]
    [InlineData(SqlAgentToolType.Firebird, "DROP TABLE IF EXISTS users")]
    public void UnprovenConditionalSyntax_DoesNotMintCatalogCertainty(
        SqlAgentToolType provider, string sql)
    {
        var result = SqlCoreFacade.AnalyzeScript(sql, provider, SqlCatalogState.KnownEmpty());

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[0].Certainty);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal("SQL_SCHEMA_UNSUPPORTED_STATEMENT", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "ALTER TABLE users ADD COLUMN missing INT NOT NULL")]
    [InlineData(SqlAgentToolType.MySQL, "ALTER TABLE users ADD COLUMN missing INT NOT NULL")]
    [InlineData(SqlAgentToolType.MsSqlServer, "ALTER TABLE users ADD missing INT NOT NULL")]
    [InlineData(SqlAgentToolType.Sqlite, "ALTER TABLE users ADD COLUMN missing INTEGER NOT NULL")]
    [InlineData(SqlAgentToolType.Oracle, "ALTER TABLE users ADD (missing NUMBER NOT NULL)")]
    [InlineData(SqlAgentToolType.Firebird, "ALTER TABLE users ADD missing INTEGER NOT NULL")]
    public void NotNullAlterWithoutDataProof_IsNotACertainCatalogDelta(
        SqlAgentToolType provider, string alter)
    {
        var typeName = provider switch
        {
            SqlAgentToolType.MsSqlServer => "INT",
            SqlAgentToolType.Oracle => "NUMBER",
            _ => "INTEGER"
        };
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users (id {typeName});" + alter + ";",
            provider, SqlCatalogState.KnownEmpty());

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[1].Certainty);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnmodelledConstraintsInvalidatePreviousExactCatalogFacts(SqlAgentToolType provider)
    {
        var typeName = provider switch
        {
            SqlAgentToolType.MsSqlServer => "INT",
            SqlAgentToolType.Oracle => "NUMBER",
            _ => "INTEGER"
        };
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users (id {typeName});" +
            $"CREATE TABLE orders (id {typeName}, CHECK(id > 0));",
            provider, SqlCatalogState.KnownEmpty());

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            result.FinalCatalog.Tables.Single(t => t.Name.Equals("users",
                StringComparison.OrdinalIgnoreCase)).Knowledge);
    }

    [Fact]
    public void SQLiteColumnDropRequiresVersionAtLeastThreeThirtyFive()
    {
        const string script =
            "CREATE TABLE users(id INTEGER, name TEXT); ALTER TABLE users DROP COLUMN name;";
        var unknown = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, unknown.Statements[1].Status);
        Assert.False(unknown.FinalCatalog.IsClosedWorld);

        var tooOld = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty(),
            new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 34)));
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, tooOld.Statements[1].Status);

        var supported = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty(),
            new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 35)));
        Assert.Empty(supported.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, supported.Statements[1].Status);
        Assert.Single(Assert.Single(supported.FinalCatalog.Tables).Columns);
    }

    [Fact]
    public void SqlServerIfExistsNeedsVersionProofFor2016OrLater()
    {
        const string sql = "DROP TABLE IF EXISTS absent";
        var unknown = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            Assert.Single(unknown.Statements).Status);

        var proven = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty(),
            new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(13, 0)));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(proven.Statements).Status);
        Assert.Empty(proven.Diagnostics);
        Assert.Empty(proven.FinalCatalog.Tables);
        Assert.Empty(proven.Statements[0].Changes);
    }

    [Fact]
    public void FirebirdNewerColumnKeywordAndBooleanRequireVersionProof()
    {
        const string script =
            "CREATE TABLE users(id INTEGER, active BOOLEAN); ALTER TABLE users DROP COLUMN active;";
        var unknown = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Firebird, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, unknown.Statements[0].Status);

        var proven = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Firebird, SqlCatalogState.KnownEmpty(),
            new SqlProviderCapabilityProfile(SqlAgentToolType.Firebird, new Version(6, 0)));
        Assert.Empty(proven.Diagnostics);
        Assert.Equal(2, proven.Statements.Count);
        Assert.All(proven.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Single(Assert.Single(proven.FinalCatalog.Tables).Columns);
    }

    [Fact]
    public void SqlServerGoBatchBoundaries_PreserveCatalogOrderWithoutGrantingExecution()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE dbo.users (id INT);\nGO\n" +
            "ALTER TABLE dbo.users ADD name NVARCHAR(32);\nGO\n" +
            "DROP TABLE dbo.users;",
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());

        Assert.Equal(3, result.Statements.Count);
        Assert.Equal(new[] { 0, 1, 2 }, result.Statements.Select(s => s.Syntax.BatchIndex));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void QuotedNames_AreNotErasedAcrossTheOracleAndFirebirdGrammar()
    {
        foreach (var provider in new[] { SqlAgentToolType.Oracle, SqlAgentToolType.Firebird })
        {
            var result = SqlCoreFacade.AnalyzeScript(
                "CREATE TABLE \"MixedCase\" (\"QuotedId\" INTEGER);",
                provider, SqlCatalogState.KnownEmpty());
            Assert.Empty(result.Diagnostics);
            var table = Assert.Single(result.FinalCatalog.Tables);
            Assert.Equal("MixedCase", table.Name);
            Assert.Equal("QuotedId", Assert.Single(table.Columns).Name);
        }
    }
}
