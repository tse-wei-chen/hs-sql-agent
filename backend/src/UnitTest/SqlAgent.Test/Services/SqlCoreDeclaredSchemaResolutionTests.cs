using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A caller-declared single-schema PostgreSQL SEARCH_PATH is an explicit
/// static-analysis premise, not evidence from a live server.
/// </summary>
public sealed class SqlCoreDeclaredSchemaResolutionTests
{
    private static SqlProviderCapabilityProfile Profile(string value) =>
        new(SqlAgentToolType.Postgres, null, default, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SEARCH_PATH"] = value
            });

    private static SqlScriptAnalysisResult Analyze(string sql, string? scope = "app") =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty(), scope is null ? null : Profile(scope));

    [Fact]
    public void SingleSearchPath_BindsUnqualifiedDdlQueriesAndMutations()
    {
        var result = Analyze(
            "CREATE TABLE users(id INTEGER, note TEXT);" +
            "SELECT note FROM users;" +
            "UPDATE users SET note = 'Ada' WHERE id = 1;" +
            "ALTER TABLE users ADD COLUMN active BOOLEAN;" +
            "SELECT active FROM users;");
        Assert.Equal(5, result.Statements.Count);
        Assert.All(result.Statements, s => Assert.Empty(s.Diagnostics));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[1].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[4].CatalogBinding);
        var table = Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(new[] {"app", "users"}, table.QualifiedName.Parts);
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(2, result.Statements[3].CatalogBefore.Tables[0].Columns.Count);
    }

    [Fact]
    public void SearchPathSelectsDeclaredSchemaWithoutAffectingOtherNamedSchemas()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER, primary_name TEXT);" +
            "CREATE TABLE archive.users(id INTEGER, archive_name TEXT);" +
            "SELECT primary_name FROM users;" +
            "SELECT archive_name FROM archive.users;" +
            "SELECT archive_name FROM users;" +
            "DROP TABLE users;" +
            "SELECT archive_name FROM archive.users;");
        Assert.Equal(7, result.Statements.Count);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[3].CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, result.Statements[4].NameBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
        Assert.Equal("archive.users", Assert.Single(result.FinalCatalog.Tables).Name);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[6].CatalogBinding);
    }

    [Fact]
    public void SearchPathResolvesForeignKeyParentAndProtectsActualObject()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));" +
            "DROP TABLE parent;" +
            "DROP TABLE child;" +
            "DROP TABLE parent;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        var child = result.Statements[1].CatalogAfter.Tables.Single(t => t.Name == "app.child");
        var fk = Assert.Single(child.Constraints);
        Assert.Equal(new [] { "app", "parent" }, fk.ReferencedIdentity.Parts);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void EveryProvenDeltaHasExactTableIdentityNotJustDisplayName()
    {
        var result = Analyze(
            "CREATE TABLE \"app.orders\"(id INTEGER);" +
            "CREATE TABLE app.orders(id INTEGER, note TEXT);" +
            "ALTER TABLE app.orders DROP COLUMN note;" +
            "DROP TABLE \"app.orders\";" +
            "DROP TABLE app.orders;",
            null);
        Assert.All(result.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var changes = result.Statements.SelectMany(s => s.Changes).ToArray();
        Assert.Equal(5, changes.Length);
        Assert.All(changes, d =>
        {
            Assert.Equal(SqlCatalogKnowledge.Known, d.Certainty);
            Assert.NotNull(d.TableIdentity);
            Assert.Equal("app.orders", d.TableName);
            Assert.Equal("app.orders", d.TableIdentity.DisplayName);
        });
        Assert.Single(changes[0].TableIdentity.Parts);
        Assert.Equal(2, changes[1].TableIdentity.Parts.Count);
        Assert.NotEqual(changes[0].TableIdentity.Key, changes[1].TableIdentity.Key);
        Assert.Equal(changes[1].TableIdentity.Key, changes[2].TableIdentity.Key);
        Assert.Equal(changes[0].TableIdentity.Key, changes[3].TableIdentity.Key);
        Assert.Equal(changes[1].TableIdentity.Key, changes[4].TableIdentity.Key);
        Assert.Equal("note", changes[2].ColumnName);
    }

    [Fact]
    public void SearchPathDoesNotAddAliasedCatalogEntriesOrPhantomDeltas()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "SELECT id FROM users;" +
            "SELECT id FROM users;" +
            "DROP TABLE users;");
        Assert.Single(result.Statements[1].CatalogAfter.Tables);
        Assert.Same(result.Statements[1].CatalogBefore, result.Statements[1].CatalogAfter);
        Assert.Same(result.Statements[2].CatalogBefore, result.Statements[2].CatalogAfter);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Equal("app.users", Assert.Single(result.Statements[0].Changes).TableIdentity.DisplayName);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData("app,public")]
    [InlineData("$user,public")]
    [InlineData("pg_catalog")]
    [InlineData("pg_temp")]
    [InlineData("information_schema")]
    [InlineData("App")]
    [InlineData("app ")]
    public void UnsupportedDeclaredPathCannotMintAnUnqualifiedCreateProof(string scope)
    {
        var result = Analyze(
            "CREATE TABLE users(id INTEGER);SELECT id FROM users;",
            scope);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.Equal("SQL_SCHEMA_DEPENDENCY_UNPROVEN", Assert.Single(result.Statements[0].Diagnostics).Code);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].CatalogBinding);
    }

    [Fact]
    public void DeclaredScopeStillDoesNotProvePostgresImplicitSystemCatalog()
    {
        var ddl = Analyze("CREATE TABLE pg_class(id INTEGER);");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, ddl.Statements[0].Status);
        Assert.False(ddl.FinalCatalog.IsClosedWorld);
        var query = Analyze("SELECT * FROM pg_class;");
        Assert.Equal(SqlCatalogBindingStatus.Unverified, query.Statements[0].CatalogBinding);
        Assert.Equal("SQL_CATALOG_SCHEMA_SYSTEM_UNPROVEN",
            Assert.Single(query.Statements[0].Diagnostics).Code);
    }

    [Fact]
    public void WithoutDeclaredPathUnqualifiedLookupRemainsConservative()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);SELECT id FROM users;",
            null);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].NameBinding);
        Assert.Equal("SQL_CATALOG_DEFAULT_SCHEMA_UNPROVEN",
            Assert.Single(result.Statements[1].Diagnostics).Code);
    }

    [Fact]
    public void SearchPathWithCallerUnqualifiedCatalogFactsCannotClaimProof()
    {
        var known = Analyze("CREATE TABLE users(id INTEGER);", null).FinalCatalog;
        var analysis = SqlCoreFacade.AnalyzeScript(
            "SELECT id FROM users;", SqlAgentToolType.Postgres, known, Profile("app"));
        Assert.Equal(SqlCatalogBindingStatus.Unverified, analysis.Statements[0].CatalogBinding);
        Assert.Equal("SQL_CATALOG_SCHEMA_SCOPE_UNPROVEN",
            Assert.Single(analysis.Statements[0].Diagnostics).Code);
    }

    [Fact]
    public void TransactionRollbackRestoresScopedTableIdentity()
    {
        var result = Analyze(
            "CREATE TABLE app.users(id INTEGER);" +
            "BEGIN;" +
            "ALTER TABLE users ADD COLUMN extra TEXT;" +
            "ROLLBACK;" +
            "SELECT id FROM users;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        var table = Assert.Single(result.FinalCatalog.Tables);
        Assert.Equal(new[] { "app", "users" }, table.QualifiedName.Parts);
        Assert.Single(table.Columns);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[4].CatalogBinding);
    }

    [Fact]
    public void OtherDialectsDoNotInterpretPostgresqlSearchPathSetting()
    {
        var settings = new Dictionary<string, string> { ["SEARCH_PATH"] = "app" };
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Oracle, null, default, null, settings);
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE APP.USERS(ID NUMBER);SELECT ID FROM USERS;",
            SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty(), profile);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, result.Statements[1].NameBinding);
    }
}
