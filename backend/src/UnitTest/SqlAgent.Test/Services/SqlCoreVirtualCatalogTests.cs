using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.Models;
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
            "CREATE TABLE orders (id INTEGER, CHECK(id > 0));" +
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
            "CREATE TABLE users(id INT, CHECK(id > 0))", SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty());

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

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void GrowingCatalogPreservesOrderedSnapshotsAndStructuredKeys(SqlAgentToolType provider)
    {
        // Reverse insertion order deliberately exercises the middle/front
        // insertion path, not only the append-fast path.
        var statements = Enumerable.Range(0, 32).Reverse()
            .Select(index => provider == SqlAgentToolType.MsSqlServer
                ? $"CREATE TABLE dbo.t_{index:000} (id INT);"
                : $"CREATE TABLE t_{index:000} (id INTEGER);");
        var profile = provider == SqlAgentToolType.MsSqlServer
            ? new SqlProviderCapabilityProfile(provider, new Version(16, 0), default, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["CATALOG_COLLATION"] = "Latin1_General_100_CI_AS" })
            : null;
        var result = SqlCoreFacade.AnalyzeScript(
            string.Join(" ", statements), provider, SqlCatalogState.KnownEmpty(), profile);

        Assert.Equal(32, result.Statements.Count);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogKnowledge.Known, result.FinalCatalog.Knowledge);
        Assert.Equal(32, result.FinalCatalog.Tables.Count);

        var keys = result.FinalCatalog.Tables.Select(table => table.QualifiedName.Key).ToArray();
        Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);

        // Each statement must expose its own persistent pre/post Catalog.
        for (var index = 0; index < result.Statements.Count; index++)
        {
            Assert.Equal(index, result.Statements[index].CatalogBefore.Tables.Count);
            Assert.Equal(index + 1, result.Statements[index].CatalogAfter.Tables.Count);
        }
        Assert.Empty(result.Statements[0].CatalogBefore.Tables);
        Assert.Single(result.Statements[0].CatalogAfter.Tables);

        // A structured key is immutable and allocated only once per identity.
        var identity = result.FinalCatalog.Tables[0].QualifiedName;
        Assert.Same(identity.Key, identity.Key);
    }

    [Fact]
    public void CatalogOrderedUpdatePreservesReplacementDeletionAndRollback()
    {
        var result = Analyze(
            "CREATE TABLE z (id INTEGER);" +
            "CREATE TABLE a (id INTEGER);" +
            "CREATE TABLE m (id INTEGER);" +
            "ALTER TABLE m ADD COLUMN note TEXT;" +
            "DROP TABLE z;" +
            "BEGIN; CREATE TABLE b (id INTEGER); ROLLBACK;" +
            "CREATE TABLE b (id INTEGER);");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "a", "b", "m" },
            result.FinalCatalog.Tables.Select(table => table.Name));
        Assert.Equal(new[] { "id", "note" },
            result.FinalCatalog.Tables.Single(table => table.Name == "m").Columns.Select(column => column.Name));
        Assert.Equal(new[] { "a", "m", "z" },
            result.Statements[3].CatalogAfter.Tables.Select(table => table.Name));
        Assert.Equal(new[] { "a", "m" },
            result.Statements[7].CatalogAfter.Tables.Select(table => table.Name));
        Assert.Equal(2, result.Statements[5].CatalogAfter.Tables.Count);
        Assert.Equal(3, result.Statements[6].CatalogAfter.Tables.Count);
        Assert.Equal(2, result.Statements[7].CatalogAfter.Tables.Count);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CatalogFindsFirstMiddleAndLastIdentityThroughOrderedMutations(SqlAgentToolType provider)
    {
        var prefix = provider == SqlAgentToolType.MsSqlServer ? "dbo." : "";
        var columnType = provider == SqlAgentToolType.MsSqlServer ? "INT" : "INTEGER";
        var statements = Enumerable.Range(0, 32).Reverse()
            .Select(i => $"CREATE TABLE {prefix}t_{i:000} (id {columnType});")
            .ToList();
        statements.Add($"DROP TABLE {prefix}t_000;");
        statements.Add($"DROP TABLE {prefix}t_016;");
        statements.Add($"DROP TABLE {prefix}t_031;");
        statements.Add($"CREATE TABLE {prefix}t_016 (id {columnType});");
        var profile = provider == SqlAgentToolType.MsSqlServer
            ? new SqlProviderCapabilityProfile(provider, new Version(16, 0), default, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["CATALOG_COLLATION"] = "Latin1_General_100_CI_AS" })
            : null;
        var result = SqlCoreFacade.AnalyzeScript(
            string.Join(" ", statements), provider, SqlCatalogState.KnownEmpty(), profile);

        Assert.Equal(36, result.Statements.Count);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Equal(32, result.Statements[31].CatalogAfter.Tables.Count);
        Assert.Equal(31, result.Statements[32].CatalogAfter.Tables.Count);
        Assert.Equal(30, result.Statements[33].CatalogAfter.Tables.Count);
        Assert.Equal(29, result.Statements[34].CatalogAfter.Tables.Count);
        Assert.Equal(30, result.FinalCatalog.Tables.Count);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        var names = result.FinalCatalog.Tables.Select(t => t.QualifiedName.Key).ToArray();
        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal), names);
        Assert.DoesNotContain(result.FinalCatalog.Tables, t => t.Name.EndsWith("t_000", StringComparison.Ordinal));
        Assert.DoesNotContain(result.FinalCatalog.Tables, t => t.Name.EndsWith("t_031", StringComparison.Ordinal));
        Assert.Single(result.FinalCatalog.Tables.Where(t => t.Name.EndsWith("t_016", StringComparison.Ordinal)));
        Assert.Equal(32, result.Statements[31].CatalogAfter.Tables.Count);
    }
}
