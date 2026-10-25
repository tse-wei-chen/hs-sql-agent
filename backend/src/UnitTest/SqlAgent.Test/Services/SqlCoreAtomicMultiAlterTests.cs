using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// One ALTER TABLE is a single static Catalog transaction: all modeled
/// alterations commit together or none of their proven deltas are published.
/// </summary>
public sealed class SqlCoreAtomicMultiAlterTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres,
        "CREATE TABLE users (id INTEGER, old_col TEXT);",
        "ALTER TABLE users ADD COLUMN first_col TEXT, DROP COLUMN old_col, ADD COLUMN second_col INTEGER;",
        "users", "first_col", "second_col")]
    [InlineData(SqlAgentToolType.MySQL,
        "CREATE TABLE users (id INT, old_col TEXT);",
        "ALTER TABLE users ADD COLUMN first_col TEXT, DROP COLUMN old_col, ADD COLUMN second_col INT;",
        "users", "first_col", "second_col")]
    [InlineData(SqlAgentToolType.Firebird,
        "CREATE TABLE users (id INTEGER, old_col VARCHAR(32));",
        "ALTER TABLE users ADD first_col VARCHAR(32), DROP old_col, ADD second_col INTEGER;",
        "USERS", "FIRST_COL", "SECOND_COL")]
    public void MixedNativeAlter_AppliesAllActionsToOneImmutableCatalog(
        SqlAgentToolType provider, string create, string alter,
        string tableName, string addedName1, string addedName2)
    {
        var analysis = SqlCoreFacade.AnalyzeScript(
            create + alter, provider, SqlCatalogState.KnownEmpty());
        Assert.Equal(2, analysis.Statements.Count);
        Assert.Empty(analysis.Diagnostics);
        var mutation = analysis.Statements[1];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, mutation.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, mutation.Certainty);
        Assert.Equal(new[]
        {
            SqlCatalogOperation.AddColumn,
            SqlCatalogOperation.DropColumn,
            SqlCatalogOperation.AddColumn
        }, mutation.Changes.Select(change => change.Operation));
        Assert.Equal(new[]
        {
            addedName1,
            provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL ? "old_col" : "OLD_COL",
            addedName2
        }, mutation.Changes.Select(change => change.ColumnName));
        var before = Assert.Single(mutation.CatalogBefore.Tables);
        var after = Assert.Single(mutation.CatalogAfter.Tables);
        Assert.Equal(tableName, after.Name);
        Assert.Equal(2, before.Columns.Count);
        Assert.Equal(3, after.Columns.Count);
        Assert.Equal(new[] { addedName1, addedName2 },
            after.Columns.Skip(1).Select(column => column.Name));
        Assert.All(mutation.Changes, change =>
            Assert.Equal(mutation.Syntax.Span.Start, change.Span.Start));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer,
        "CREATE TABLE users (id INT);",
        "ALTER TABLE users ADD name NVARCHAR(32), active BIT;",
        "ALTER TABLE users DROP COLUMN name, active;")]
    [InlineData(SqlAgentToolType.Oracle,
        "CREATE TABLE users (id NUMBER);",
        "ALTER TABLE users ADD (name VARCHAR2(32), active NUMBER);",
        "ALTER TABLE users DROP (name, active);")]
    public void ColumnListsUseTheirNativeDialectGrammar(
        SqlAgentToolType dialect, string create, string add, string drop)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            create + add + drop, dialect, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal(new[] { SqlCatalogOperation.AddColumn, SqlCatalogOperation.AddColumn },
            result.Statements[1].Changes.Select(change => change.Operation));
        Assert.Equal(new[] { SqlCatalogOperation.DropColumn, SqlCatalogOperation.DropColumn },
            result.Statements[2].Changes.Select(change => change.Operation));
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns);
        Assert.Equal(3, Assert.Single(result.Statements[1].CatalogAfter.Tables).Columns.Count);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER",
        "ALTER TABLE users ADD COLUMN a TEXT, ADD COLUMN a TEXT;")]
    [InlineData(SqlAgentToolType.MySQL, "INT",
        "ALTER TABLE users ADD COLUMN a TEXT, ADD COLUMN a TEXT;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT",
        "ALTER TABLE users ADD a NVARCHAR(32), a NVARCHAR(32);")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER",
        "ALTER TABLE users ADD (a VARCHAR2(32), a VARCHAR2(32));")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER",
        "ALTER TABLE users ADD a VARCHAR(32), ADD a VARCHAR(32);")]
    public void LateDuplicateAction_DoesNotCommitEarlyAddition(
        SqlAgentToolType dialect, string idType, string alter)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users(id {idType});" + alter + "SELECT id FROM users;",
            dialect, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Equal("SQL_SCHEMA_SEMANTIC_INVALID",
            Assert.Single(result.Statements[1].Diagnostics).Code);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Same(result.Statements[1].CatalogBefore, result.Statements[1].CatalogAfter);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns);
        if (dialect == SqlAgentToolType.Postgres
            || dialect == SqlAgentToolType.Oracle
            || dialect == SqlAgentToolType.Firebird)
            Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[2].NameBinding);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER",
        "ALTER TABLE users ADD COLUMN a TEXT, DROP COLUMN missing;")]
    [InlineData(SqlAgentToolType.MySQL, "INT",
        "ALTER TABLE users ADD COLUMN a TEXT, DROP COLUMN missing;")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER",
        "ALTER TABLE users ADD a VARCHAR(32), DROP missing;")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT",
        "ALTER TABLE users DROP COLUMN id, missing;")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER",
        "ALTER TABLE users DROP (id, missing);")]
    public void LaterInvalidDrop_LeavesOriginalSchemaIntact(
        SqlAgentToolType dialect, string idType, string alter)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users(id {idType});" + alter,
            dialect, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns);
    }

    [Fact]
    public void UnknownCatalog_NeverClaimsIndividualMultiAlterEffects()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "ALTER TABLE users ADD COLUMN a INTEGER, DROP COLUMN b;",
            SqlAgentToolType.Postgres);
        var stmt = Assert.Single(result.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, stmt.Certainty);
        Assert.Equal(SqlCatalogOperation.Indeterminate,
            Assert.Single(stmt.Changes).Operation);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite,
        "ALTER TABLE users ADD COLUMN a INTEGER, ADD COLUMN b TEXT;")]
    [InlineData(SqlAgentToolType.Sqlite,
        "ALTER TABLE users ADD COLUMN a INTEGER, DROP COLUMN id;")]
    [InlineData(SqlAgentToolType.Oracle,
        "ALTER TABLE users ADD (a NUMBER) DROP (id);")]
    [InlineData(SqlAgentToolType.MsSqlServer,
        "ALTER TABLE users ADD a INT, DROP COLUMN id;")]
    public void IllegalDialectMultiAlter_IsUnsupportedAndInvalidatesKnownCatalog(
        SqlAgentToolType dialect, string alter)
    {
        var typeName = dialect == SqlAgentToolType.Oracle ? "NUMBER" : "INTEGER";
        if (dialect == SqlAgentToolType.MsSqlServer) typeName = "INT";
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users(id {typeName});" + alter,
            dialect, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal("SQL_SCHEMA_UNSUPPORTED_STATEMENT",
            Assert.Single(result.Statements[1].Diagnostics).Code);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogKnowledge.Unknown,
            Assert.Single(result.FinalCatalog.Tables).Knowledge);
    }

    [Fact]
    public void PostgresMultiAlterWithinRollback_RestoresPriorCatalogExactly()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users (id INTEGER, old_name TEXT);" +
            "BEGIN;" +
            "ALTER TABLE users ADD COLUMN new_name TEXT, DROP COLUMN old_name;" +
            "ROLLBACK;" +
            "SELECT old_name FROM users;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(2, result.Statements[2].Changes.Count);
        Assert.Equal(SqlTransactionState.Outside, result.FinalTransaction);
        Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[4].CatalogBinding);
        var columns = Assert.Single(result.FinalCatalog.Tables).Columns;
        Assert.Equal(new[] { "id", "old_name" }, columns.Select(column => column.Name));
    }

    [Fact]
    public void ConditionalNoOpsDoNotMintPhantomDeltasInMultiAlter()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE users(id INTEGER, present TEXT);" +
            "ALTER TABLE users DROP COLUMN IF EXISTS absent, ADD COLUMN added TEXT;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.AddColumn,
            Assert.Single(result.Statements[1].Changes).Operation);
        Assert.Equal(3, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void RefuseToDropTheOnlyRemainingColumn(SqlAgentToolType dialect)
    {
        var typeName = dialect switch
        {
            SqlAgentToolType.MsSqlServer => "INT",
            SqlAgentToolType.Oracle => "NUMBER",
            _ => "INTEGER"
        };
        var drop = dialect switch
        {
            SqlAgentToolType.Firebird => "DROP id",
            _ => "DROP COLUMN id"
        };
        var profile = dialect == SqlAgentToolType.Sqlite
            ? new HsSqlAgent.SqlCore.Models.SqlProviderCapabilityProfile(
                dialect, new Version(3, 35))
            : null;
        var result = SqlCoreFacade.AnalyzeScript(
            $"CREATE TABLE users(id {typeName});ALTER TABLE users {drop};",
            dialect, SqlCatalogState.KnownEmpty(), profile);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Empty(result.Statements[1].Changes);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns);
    }
}
