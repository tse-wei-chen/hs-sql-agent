using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreSchemaDependencyTests
{
    private static SqlScriptAnalysisResult Analyze(
        SqlAgentToolType dialect, string script)
    {
        var profile = dialect == SqlAgentToolType.Sqlite
            ? new HsSqlAgent.SqlCore.Models.SqlProviderCapabilityProfile(
                dialect, new Version(3, 35))
            : null;
        return SqlCoreFacade.AnalyzeScript(
            script, dialect, SqlCatalogState.KnownEmpty(), profile);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "VARCHAR(32)")]
    [InlineData(SqlAgentToolType.MySQL, "INT", "VARCHAR(32)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT", "NVARCHAR(32)")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER", "VARCHAR(32)")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "VARCHAR2(32)")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER", "VARCHAR(32)")]
    public void SixSources_ProvePrimaryAndUniqueDependencies_AndGuardDrop(
        SqlAgentToolType dialect, string numericType, string textType)
    {
        var result = Analyze(dialect,
            $"CREATE TABLE users(id {numericType} NOT NULL PRIMARY KEY, " +
            $"name {textType} UNIQUE, note {textType});" +
            (dialect == SqlAgentToolType.Firebird
                ? "ALTER TABLE users DROP name; ALTER TABLE users DROP note;"
                : "ALTER TABLE users DROP COLUMN name; ALTER TABLE users DROP COLUMN note;") +
            "DROP TABLE users;");
        Assert.Equal(4, result.Statements.Count);
        Assert.Empty(result.Statements[0].Diagnostics);
        var table = Assert.Single(result.Statements[0].CatalogAfter.Tables);
        Assert.Equal(2, table.Constraints.Count);
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey, table.Constraints[0].Kind);
        Assert.Equal(SqlCatalogConstraintKind.Unique, table.Constraints[1].Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Equal("SQL_SCHEMA_SEMANTIC_INVALID", Assert.Single(result.Statements[1].Diagnostics).Code);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var after = Assert.Single(result.Statements[2].CatalogAfter.Tables);
        Assert.Equal(2, after.Columns.Count);
        Assert.Equal(2, after.Constraints.Count);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void ProvenForeignKeys_BlockParentDropAndChildColumnDrop(
        SqlAgentToolType dialect, string kind)
    {
        var result = Analyze(dialect,
            $"CREATE TABLE parent(id {kind} NOT NULL PRIMARY KEY);" +
            $"CREATE TABLE child(id {kind}, parent_id {kind}, " +
            "CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parent(id));" +
            (dialect == SqlAgentToolType.Firebird
                ? "ALTER TABLE child DROP parent_id;"
                : "ALTER TABLE child DROP COLUMN parent_id;") +
            "DROP TABLE parent;" +
            "DROP TABLE child;" +
            "DROP TABLE parent;");
        Assert.Equal(6, result.Statements.Count);
        Assert.Empty(result.Statements[0].Diagnostics);
        Assert.Empty(result.Statements[1].Diagnostics);
        var fk = Assert.Single(Assert.Single(result.Statements[1].CatalogAfter.Tables
            .Where(t => t.Name.Equals("child", StringComparison.OrdinalIgnoreCase))).Constraints);
        Assert.Equal(SqlCatalogConstraintKind.ForeignKey, fk.Kind);
        Assert.Equal(1, fk.ReferencedColumns.Count);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[5].Status);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void CompositeKeysValidateWholeListsAndCannotDropParticipatingColumns(
        SqlAgentToolType dialect, string typeName)
    {
        var result = Analyze(dialect,
            $"CREATE TABLE parent(a {typeName} NOT NULL, b {typeName} NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(a,b));" +
            $"CREATE TABLE child(id {typeName}, x {typeName}, y {typeName}, " +
            "FOREIGN KEY(x,y) REFERENCES parent(a,b));" +
            (dialect == SqlAgentToolType.Firebird
                ? "ALTER TABLE parent DROP b;"
                : "ALTER TABLE parent DROP COLUMN b;"));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal(2, result.Statements[1].CatalogAfter.Tables.Count);
        Assert.Equal(2,
            result.Statements[1].CatalogAfter.Tables.Single(t => t.Name.Equals("CHILD",
                StringComparison.OrdinalIgnoreCase)).Constraints.Single().ReferencedColumns.Count);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[2].Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void ReferencingNonUniqueTarget_FailsWithoutModifyingKnownCatalog(
        SqlAgentToolType dialect, string kind)
    {
        var result = Analyze(dialect,
            $"CREATE TABLE parent(id {kind}, value {kind});" +
            $"CREATE TABLE child(id {kind}, parent_id {kind}, " +
            "FOREIGN KEY(parent_id) REFERENCES parent(id));");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_SCHEMA_SEMANTIC_INVALID");
        Assert.Empty(result.Statements[1].Changes);
        Assert.Single(result.FinalCatalog.Tables);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    public void CollationSensitiveForeignKeyTargets_DoNotGetInventedProof(
        SqlAgentToolType dialect, string typeName)
    {
        var result = Analyze(dialect,
            $"CREATE TABLE parent(id {typeName} NOT NULL PRIMARY KEY);" +
            $"CREATE TABLE child(parent_id {typeName}, " +
            "FOREIGN KEY(parent_id) REFERENCES parent(id));");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Contains(result.Statements[1].Diagnostics,
            d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("CREATE TABLE users (id INTEGER PRIMARY KEY, PRIMARY KEY(id));")]
    [InlineData("CREATE TABLE users (id INTEGER, CONSTRAINT duplicate UNIQUE(id), CONSTRAINT duplicate UNIQUE(id));")]
    [InlineData("CREATE TABLE users (id INTEGER, PRIMARY KEY(missing));")]
    [InlineData("CREATE TABLE users (id INTEGER, FOREIGN KEY(id) REFERENCES absent(id));")]
    public void InvalidConstraintDefinitions_RejectWithoutPublishingCatalog(string sql)
    {
        var result = Analyze(SqlAgentToolType.Postgres, sql);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Tables);
        Assert.Empty(result.Statements[0].Changes);
    }

    [Theory]
    [InlineData("CREATE TABLE users(id INTEGER, CHECK(id > 0));")]
    [InlineData("CREATE TABLE users(id INTEGER DEFAULT 1);")]
    [InlineData("CREATE TABLE users(id INTEGER PRIMARY KEY ON CONFLICT IGNORE);")]
    public void UnmodelledConstraintsOrModifiers_FailClosed(string sql)
    {
        var result = Analyze(SqlAgentToolType.Postgres, sql);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[0].Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[0].Certainty);
    }

    [Fact]
    public void MultiAlterLaterDependencyFailure_IsAtomic()
    {
        var result = Analyze(SqlAgentToolType.Postgres,
            "CREATE TABLE users(id INTEGER NOT NULL PRIMARY KEY, email TEXT UNIQUE, note TEXT);" +
            "ALTER TABLE users DROP COLUMN note, DROP COLUMN email;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Same(result.Statements[1].CatalogBefore, result.Statements[1].CatalogAfter);
        Assert.Equal(3, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
        Assert.Equal(2, Assert.Single(result.FinalCatalog.Tables).Constraints.Count);
    }

    [Fact]
    public void PostgreSqlRollbackRestoresCompleteConstraintGraph()
    {
        var result = Analyze(SqlAgentToolType.Postgres,
            "CREATE TABLE parent(id INTEGER NOT NULL PRIMARY KEY);" +
            "BEGIN;" +
            "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));" +
            "ROLLBACK;" +
            "DROP TABLE parent;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].CatalogAfter.Tables.Where(t => t.Name == "child"));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void SelfReferencingForeignKey_UsesCurrentCreatePrimaryKey()
    {
        var result = Analyze(SqlAgentToolType.Postgres,
            "CREATE TABLE nodes(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES nodes(id));" +
            "DROP TABLE nodes;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.Equal(2, Assert.Single(result.Statements[0].CatalogAfter.Tables).Constraints.Count);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
    }
}
