using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreIndexConstraintLifecycleTests
{
    private static SqlScriptAnalysisResult Analyze(SqlAgentToolType dialect, string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "CREATE INDEX idx_users_id ON users(id);", "DROP INDEX idx_users_id;")]
    [InlineData(SqlAgentToolType.MySQL, "INT", "CREATE INDEX idx_users_id ON users(id);", "DROP INDEX idx_users_id ON users;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT", "CREATE INDEX idx_users_id ON users(id);", "DROP INDEX idx_users_id ON users;")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER", "CREATE INDEX idx_users_id ON users(id);", "DROP INDEX idx_users_id;")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "CREATE INDEX idx_users_id ON users(id);", "DROP INDEX idx_users_id;")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER", "CREATE INDEX idx_users_id ON users(id);", "DROP INDEX idx_users_id;")]
    public void SixSourceStandaloneIndexLifecycle_ProtectsColumnUntilDropped(
        SqlAgentToolType provider, string numeric, string createIndex, string dropIndex)
    {
        var script = $"CREATE TABLE users(id {numeric}, other {numeric});" +
                     createIndex +
                     "DROP TABLE missing;" + // Known invalid, no Catalog mutation
                     dropIndex;
        var result = Analyze(provider, script);
        Assert.Equal(4, result.Statements.Count);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        var afterCreate = Assert.Single(result.Statements[1].CatalogAfter.Tables);
        var idx = Assert.Single(afterCreate.Indexes);
        Assert.Equal(SqlCatalogOperation.CreateIndex, Assert.Single(result.Statements[1].Changes).Operation);
        Assert.Empty(result.Statements[1].CatalogBefore.Tables[0].Indexes);
        Assert.Single(idx.Columns);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.Single(afterCreate.Indexes); // immutable snapshot
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "DROP INDEX idx_users_id;")]
    [InlineData(SqlAgentToolType.MySQL, "INT", "DROP INDEX idx_users_id ON users;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT", "DROP INDEX idx_users_id ON users;")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER", "DROP INDEX idx_users_id;")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "DROP INDEX idx_users_id;")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER", "DROP INDEX idx_users_id;")]
    public void IndexDependencyBlocksDrop_AndNoPhantomDeltaLeaks(
        SqlAgentToolType provider, string typeName, string dropIndex)
    {
        var dropColumn = provider == SqlAgentToolType.Firebird
            ? "ALTER TABLE users DROP id;"
            : "ALTER TABLE users DROP COLUMN id;";
        var profile = provider == SqlAgentToolType.Sqlite
            ? new HsSqlAgent.SqlCore.Models.SqlProviderCapabilityProfile(provider, new Version(3, 35))
            : null;
        var analysis = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users(id {typeName}, extra {typeName});" +
            "CREATE INDEX idx_users_id ON users(id);" +
            dropColumn + dropIndex + dropColumn,
            provider, SqlCatalogState.KnownEmpty(), profile);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, analysis.Statements[2].Status);
        Assert.Empty(analysis.Statements[2].Changes);
        Assert.Single(Assert.Single(analysis.Statements[2].CatalogAfter.Tables).Indexes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, analysis.Statements[4].Status);
        Assert.Single(Assert.Single(analysis.FinalCatalog.Tables).Columns);
        Assert.Empty(Assert.Single(analysis.FinalCatalog.Tables).Indexes);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "idx_users")]
    [InlineData(SqlAgentToolType.MySQL, "INT", "idx_users")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT", "IDX_USERS")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER", "IDX_USERS")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "IDX_USERS")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER", "IDX_USERS")]
    public void DuplicateIndexesDoNotReplaceKnownImmutableFacts(
        SqlAgentToolType provider, string typeName, string expected)
    {
        var analysis = Analyze(provider,
            $"CREATE TABLE users(id {typeName});" +
            "CREATE INDEX idx_users ON users(id);" +
            "CREATE INDEX idx_users ON users(id);");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, analysis.Statements[2].Status);
        Assert.Empty(analysis.Statements[2].Changes);
        Assert.Equal(expected, Assert.Single(Assert.Single(analysis.FinalCatalog.Tables).Indexes).Name);
        Assert.Same(analysis.Statements[2].CatalogBefore, analysis.Statements[2].CatalogAfter);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void IndexNamesOnlyCannotEraseImplicitKeyIndexes(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE users(id {typeName} NOT NULL PRIMARY KEY);" +
            "DROP INDEX unknown;");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        // Name-only DROP cannot rule out an implicit key index; MySQL/SQL
        // Server also require the provider-native ON table syntax.
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[1].Certainty);
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey,
            Assert.Single(Assert.Single(result.Statements[0].CatalogAfter.Tables).Constraints).Kind);
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX idx ON users(id);")]
    [InlineData("CREATE INDEX idx ON users((id + 1));")]
    [InlineData("CREATE INDEX idx ON users(id) WHERE id > 0;")]
    public void UnmodeledIndexCapabilitiesInvalidateCatalogProofs(string sql)
    {
        var analysis = Analyze(SqlAgentToolType.Postgres,
            "CREATE TABLE users(id INTEGER);" + sql);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, analysis.Statements[1].Status);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    public void DropForeignKeyConstraintUnlocksParentDrop(
        SqlAgentToolType provider, string typeName)
    {
        var script =
            $"CREATE TABLE parent(id {typeName} NOT NULL PRIMARY KEY);" +
            $"CREATE TABLE child(parent_id {typeName}, " +
            "CONSTRAINT fk_child_parent FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "DROP TABLE parent;" +
            "ALTER TABLE child DROP CONSTRAINT fk_child_parent;" +
            "DROP TABLE parent;";
        var result = Analyze(provider, script);
        if (provider == SqlAgentToolType.MsSqlServer)
        {
            Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
            return; // Collation proof deliberately missing
        }
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlCatalogOperation.DropConstraint,
            Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.FinalCatalog.Tables.Where(t => t.Name.Equals("parent",
            StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ReferencedPrimaryKeyCannotBeDroppedWhileChildForeignKeyExists()
    {
        var result = Analyze(SqlAgentToolType.Postgres,
            "CREATE TABLE parent(id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE child(parent_id INTEGER, CONSTRAINT fk_child FOREIGN KEY(parent_id) REFERENCES parent(id));" +
            "ALTER TABLE parent DROP CONSTRAINT pk_parent;" +
            "ALTER TABLE child DROP CONSTRAINT fk_child;" +
            "ALTER TABLE parent DROP CONSTRAINT pk_parent;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables.Where(t => t.Name == "parent")).Constraints);
    }

    [Theory]
    [InlineData("ALTER TABLE users ADD CONSTRAINT uniq_id UNIQUE(id);")]
    [InlineData("ALTER TABLE users ADD CONSTRAINT pk_id PRIMARY KEY(id);")]
    public void AddConstraintCannotProveExistingRowData(string sql)
    {
        var result = Analyze(SqlAgentToolType.Postgres,
            "CREATE TABLE users(id INTEGER);" + sql);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal("SQL_SCHEMA_DEPENDENCY_UNPROVEN",
            Assert.Single(result.Statements[1].Diagnostics).Code);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void PostgresRollbackRestoresIndexGraph()
    {
        var result = Analyze(SqlAgentToolType.Postgres,
            "CREATE TABLE users(id INTEGER);" +
            "BEGIN;" +
            "CREATE INDEX idx_users ON users(id);" +
            "ROLLBACK;" +
            "ALTER TABLE users ADD COLUMN note TEXT;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(Assert.Single(result.Statements[3].CatalogAfter.Tables).Indexes);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
    }
}
