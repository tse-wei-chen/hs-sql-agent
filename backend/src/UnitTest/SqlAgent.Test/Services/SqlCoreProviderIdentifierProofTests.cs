using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreProviderIdentifierProofTests
{
    private static SqlProviderCapabilityProfile Profile(SqlAgentToolType provider, string key, string value) =>
        new(provider, null, default, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value });

    private static SqlScriptAnalysisResult Analyze(
        SqlAgentToolType provider, string sql, SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, SqlCatalogState.KnownEmpty(), profile);

    [Theory]
    [InlineData("1")]
    public void MySqlModeOne_UnifiesDdlQueryAndMutationCase(string mode)
    {
        var profile = Profile(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", mode);
        var result = Analyze(SqlAgentToolType.MySQL,
            "CREATE TABLE Users (Id INT, Name VARCHAR(20));" +
            "SELECT id FROM uSeRs;" +
            "UPDATE USERS SET Name = 'Ada' WHERE ID = 1;" +
            "UPDATE users SET ID = 'not numeric' WHERE ID = 1;" +
            "ALTER TABLE users ADD COLUMN Status INT;" +
            "SELECT STATUS FROM Users;",
            profile);
        Assert.Equal(6, result.Statements.Count);
        Assert.Empty(result.Statements[0].Diagnostics);
        Assert.Equal("USERS", Assert.Single(result.FinalCatalog.Tables).Name);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[3].TypeBinding);
        Assert.Contains(result.Statements[3].Diagnostics, d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[5].CatalogBinding);
    }

    [Fact]
    public void MySqlModeOne_ProvesForeignKeysAndBlocksParentDrop()
    {
        var profile = Profile(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", "1");
        var result = Analyze(SqlAgentToolType.MySQL,
            "CREATE TABLE Parent (Id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE Child (parent_id INT, CONSTRAINT fk_parent FOREIGN KEY(parent_id) REFERENCES PARENT(ID));" +
            "DROP TABLE pArEnT;" +
            "DROP TABLE CHILD;" +
            "DROP TABLE PARENT;",
            profile);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void MySqlModeOne_RejectsCaseCollidingTableAndColumnNames()
    {
        var profile = Profile(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", "1");
        var tables = Analyze(SqlAgentToolType.MySQL,
            "CREATE TABLE Users(id INT);CREATE TABLE users(id INT);", profile);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, tables.Statements[1].Status);
        Assert.Single(tables.FinalCatalog.Tables);

        var columns = Analyze(SqlAgentToolType.MySQL,
            "CREATE TABLE Users(Id INT, ID INT);", profile);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, columns.Statements[0].Status);
        Assert.Empty(columns.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("invalid")]
    public void MySqlOtherModes_StillCannotProvePhysicalNames(string mode)
    {
        var profile = Profile(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", mode);
        var result = Analyze(SqlAgentToolType.MySQL,
            "CREATE TABLE Users(id INT);SELECT id FROM users;", profile);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_CATALOG_BINDING_IDENTIFIER_RULES_UNPROVEN");
    }

    [Fact]
    public void SqlServerKnownCaseInsensitiveCatalog_ProvesAsciiNamesAndForeignKeys()
    {
        var profile = Profile(SqlAgentToolType.MsSqlServer,
            "CATALOG_COLLATION", "Latin1_General_100_CI_AS");
        var result = Analyze(SqlAgentToolType.MsSqlServer,
            "CREATE TABLE Parent(Id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE Child(parent_id INT, FOREIGN KEY(parent_id) REFERENCES pArEnT(ID));" +
            "SELECT PARENT_ID FROM child;" +
            "DROP TABLE PARENT;" +
            "DROP TABLE child;" +
            "DROP TABLE parent;",
            profile);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
    }

    [Theory]
    [InlineData("Latin1_General_100_CS_AS")]
    [InlineData("Latin1_General_100_CI_AI")]
    [InlineData("Turkish_100_CI_AS")]
    [InlineData("unexpected_CI_AS")]
    public void SqlServerUnmodeledCollation_RemainsUnverified(string collation)
    {
        var profile = Profile(SqlAgentToolType.MsSqlServer,
            "CATALOG_COLLATION", collation);
        var result = Analyze(SqlAgentToolType.MsSqlServer,
            "CREATE TABLE Users(Id INT);SELECT id FROM users;", profile);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_CATALOG_BINDING_IDENTIFIER_RULES_UNPROVEN");
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", "1")]
    [InlineData(SqlAgentToolType.MsSqlServer, "CATALOG_COLLATION", "Latin1_General_100_CI_AS")]
    public void UnicodeIdentifiers_StayUnverifiedWithoutCollationEngine(
        SqlAgentToolType provider, string setting, string mode)
    {
        var profile = Profile(provider, setting, mode);
        var result = Analyze(provider,
            "CREATE TABLE résumé(id INT);SELECT id FROM résumé;",
            profile);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", "1",
        "CREATE TABLE `a.b` (id INT);SELECT id FROM `a.b`;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "CATALOG_COLLATION", "Latin1_General_100_CI_AS",
        "CREATE TABLE [a.b] (id INT);SELECT id FROM [a.b];")]
    [InlineData(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", "1",
        "CREATE TABLE db.users (id INT);SELECT id FROM db.users;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "CATALOG_COLLATION", "Latin1_General_100_CI_AS",
        "CREATE TABLE dbo.users (id INT);SELECT id FROM dbo.users;")]
    public void QualifiedAndDottedQuotedNamesNeedStructuredIdentifierProof(
        SqlAgentToolType provider, string key, string setting, string sql)
    {
        var result = Analyze(provider, sql, Profile(provider, key, setting));
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].NameBinding);
        if (sql.Contains("a.b", StringComparison.Ordinal))
        {
            // A quoted dot is an identifier character, not a schema-part
            // separator. The DDL is rejected before it can mint Catalog facts.
            Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
            Assert.Contains(result.Statements[1].Diagnostics,
                d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
        }
        else
        {
            // A qualified physical table can be analyzed structurally, but
            // flat Catalog names cannot prove multi-part identity resolution.
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
            Assert.Contains(result.Statements[1].Diagnostics,
                d => d.Code == "SQL_CATALOG_BINDING_IDENTIFIER_RULES_UNPROVEN");
        }
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "LOWER_CASE_TABLE_NAMES", "1")]
    [InlineData(SqlAgentToolType.MsSqlServer, "CATALOG_COLLATION", "Latin1_General_100_CI_AS")]
    public void DeclaredProfileCannotGrantExecutableScript(
        SqlAgentToolType provider, string setting, string mode)
    {
        var profile = Profile(provider, setting, mode);
        var result = Analyze(provider,
            "CREATE TABLE users(id INT);SELECT id FROM users;", profile);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, result.Statements[1].Status);
        Assert.DoesNotContain(typeof(SqlStatementAnalysis).GetProperties(),
            property => property.Name.Contains("Command", StringComparison.Ordinal)
                        || property.Name.Contains("Evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void MismatchedProviderSettingCannotBeAcceptedAsIdentifierProof()
    {
        var profile = Profile(SqlAgentToolType.Postgres, "LOWER_CASE_TABLE_NAMES", "1");
        Assert.Throws<ArgumentException>(() =>
            Analyze(SqlAgentToolType.MySQL,
                "CREATE TABLE users(id INT);SELECT id FROM users;", profile));
    }
}
