using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreVirtualCatalogTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Fact]
    public void Postgres_CreateAlterDrop_TransformsImmutableCatalogInScriptOrder()
    {
        var sql = "CREATE TABLE users (id INTEGER NOT NULL, name TEXT);" +
                  "ALTER TABLE users ADD COLUMN active BOOLEAN;" +
                  "SELECT id FROM users;" +
                  "ALTER TABLE users DROP COLUMN name;" +
                  "DROP TABLE users;";
        var analysis = Analyze(sql);

        Assert.Equal(5, analysis.Statements.Count);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, analysis.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[4].Status);
        Assert.Equal(SqlEffectKind.ChangeSchema, analysis.Statements[0].Effects);
        Assert.Equal(SqlEffectKind.ReadData, analysis.Statements[2].Effects);
        Assert.True(analysis.Statements[0].CatalogBefore.IsClosedWorld);
        Assert.Empty(analysis.Statements[0].CatalogBefore.Tables);
        var afterCreate = Assert.Single(analysis.Statements[0].CatalogAfter.Tables);
        Assert.Equal(SqlCatalogKnowledge.Known, afterCreate.Knowledge);
        Assert.Equal(new[] { "id", "name" }, afterCreate.Columns.Select(c => c.Name));
        Assert.False(afterCreate.Columns[0].Nullable);
        var afterAdd = Assert.Single(analysis.Statements[1].CatalogAfter.Tables);
        Assert.Equal(new[] { "id", "name", "active" }, afterAdd.Columns.Select(c => c.Name));
        Assert.Equal("BOOLEAN", afterAdd.Columns[2].TypeName);
        Assert.Equal(3, Assert.Single(analysis.Statements[2].CatalogBefore.Tables).Columns.Count);
        Assert.Equal(new[] { "id", "active" },
            Assert.Single(analysis.Statements[3].CatalogAfter.Tables).Columns.Select(c => c.Name));
        Assert.Empty(analysis.FinalCatalog.Tables);
        Assert.Equal(SqlCatalogKnowledge.Known, analysis.FinalCatalog.Knowledge);
        // Persistency: prior statements cannot be retroactively mutated.
        Assert.Equal(2, afterCreate.Columns.Count);
        Assert.Equal(3, afterAdd.Columns.Count);
    }

    [Fact]
    public void DefaultAnalysis_DoesNotAssumeAnEmptyDatabase()
    {
        var analysis = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users(id INTEGER); ALTER TABLE users ADD COLUMN name TEXT;",
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, analysis.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, analysis.Statements[1].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            Assert.Single(analysis.FinalCatalog.Tables).Knowledge);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void Postgres_ConditionalCreate_DoesNotOverwriteExistingKnownColumns()
    {
        var analysis = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "CREATE TABLE IF NOT EXISTS users (different_column TEXT);");

        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[1].Status);
        Assert.Empty(analysis.Statements[1].Changes);
        Assert.Equal("id", Assert.Single(Assert.Single(analysis.FinalCatalog.Tables).Columns).Name);
    }

    [Fact]
    public void Postgres_UnknownConstraint_InvalidatesPriorExactFacts()
    {
        var analysis = Analyze(
            "CREATE TABLE users (id INTEGER);" +
            "CREATE TABLE orders (id INTEGER PRIMARY KEY);" +
            "ALTER TABLE users ADD COLUMN name TEXT;");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, analysis.Statements[1].Status);
        Assert.Equal("SQL_SCHEMA_UNSUPPORTED_STATEMENT", analysis.Diagnostics[0].Code);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, analysis.Statements[2].Status);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            analysis.FinalCatalog.Tables.Single(t => t.Name == "users").Knowledge);
    }

    [Fact]
    public void Postgres_InvalidDdl_DoesNotApplyAChangeOrDestroyKnownCatalog()
    {
        var analysis = Analyze("CREATE TABLE users (id INTEGER, id TEXT); SELECT id FROM users;");

        Assert.Equal(SqlStatementAnalysisStatus.Invalid, analysis.Statements[0].Status);
        Assert.Contains(analysis.Diagnostics, d => d.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        // A failed CREATE must not invent a table for the following SELECT.
        Assert.Equal(SqlCatalogBindingStatus.Rejected, analysis.Statements[1].CatalogBinding);
        Assert.Contains(analysis.Statements[1].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TABLE_NOT_FOUND");
        Assert.Empty(analysis.Statements[0].Changes);
        Assert.Empty(analysis.FinalCatalog.Tables);
        Assert.Equal(SqlCatalogKnowledge.Known, analysis.FinalCatalog.Knowledge);
    }

    [Fact]
    public void Postgres_KnownAbsentDrop_RequiresIfExists()
    {
        var invalid = Analyze("DROP TABLE missing;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, invalid.Statements[0].Status);
        var accepted = Analyze("DROP TABLE IF EXISTS missing;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, accepted.Statements[0].Status);
        Assert.Empty(accepted.Statements[0].Changes);
        Assert.Empty(accepted.Diagnostics);
        Assert.Empty(accepted.FinalCatalog.Tables);
    }

    [Fact]
    public void Postgres_AlterDropColumnIfExists_DoesNotProducePhantomDeltas()
    {
        var analysis = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "ALTER TABLE users DROP COLUMN IF EXISTS missing;");

        Assert.Empty(analysis.Diagnostics);
        Assert.Empty(analysis.Statements[1].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[1].Status);
        Assert.Equal("id", Assert.Single(Assert.Single(analysis.FinalCatalog.Tables).Columns).Name);
    }

    [Fact]
    public void Postgres_UnsupportedAlter_DoesNotKeepPreviouslyKnownColumns()
    {
        var analysis = Analyze(
            "CREATE TABLE users(id INTEGER);" +
            "ALTER TABLE users ADD COLUMN name TEXT DEFAULT 'Ada';");

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, analysis.Statements[1].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            Assert.Single(analysis.FinalCatalog.Tables).Knowledge);
    }

    [Fact]
    public void UnsupportedConstraintInOtherDialect_FailsClosed()
    {
        var analysis = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users(id INT PRIMARY KEY)", SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, analysis.Statements[0].Status);
        Assert.Equal("SQL_SCHEMA_UNSUPPORTED_STATEMENT", Assert.Single(analysis.Diagnostics).Code);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnsupportedDdlCannotObtainAnExecutableCommand()
    {
        var analysis = Analyze("CREATE TABLE users(id INTEGER DEFAULT 1);");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, analysis.Statements[0].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, analysis.Statements[0].Certainty);
        Assert.All(analysis.Statements[0].Changes,
            delta => Assert.Equal(SqlCatalogKnowledge.Unknown, delta.Certainty));
        Assert.NotEqual(typeof(CompiledSqlCommand), analysis.GetType());
    }

    [Fact]
    public void Postgres_QualifiedQuotedNamesAndSourceSpansArePreserved()
    {
        const string sql = "-- ddl;\n CREATE TABLE public.\"Orders\" (\"OrderId\" BIGINT NOT NULL);";
        var analysis = Analyze(sql);
        var statement = Assert.Single(analysis.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal("public.Orders", Assert.Single(analysis.FinalCatalog.Tables).Name);
        Assert.Equal("OrderId", Assert.Single(Assert.Single(analysis.FinalCatalog.Tables).Columns).Name);
        Assert.Equal(statement.Syntax.Span.Start, Assert.Single(statement.Changes).Span.Start);
        Assert.True(statement.Syntax.Span.Start > 0);
    }
}
