using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// T-SQL DROP INDEX IF EXISTS is table-local, SQL Server 2016+ only.
/// An index on another table is not a match, and IF EXISTS is not permission
/// to assume a missing table, implicit constraint-backed index, or schema.
/// </summary>
public sealed class SqlCoreSqlServerConditionalDropIndexTests
{
    private static SqlProviderCapabilityProfile Version(int major) =>
        new(SqlAgentToolType.MsSqlServer, new Version(major, 0));

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlProviderCapabilityProfile? profile = null) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MsSqlServer,
            catalog ?? SqlCatalogState.KnownEmpty(), profile);

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(17)]
    public void ExistingStandaloneIndexIsExactlyRemoved(int version)
    {
        const string sql =
            "CREATE TABLE orders (id INT, state NVARCHAR(64));" +
            "CREATE INDEX ix_state ON orders(state);" +
            "DROP INDEX IF EXISTS ix_state ON orders;";
        var result = Analyze(sql, profile: Version(version));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Statements.Count);
        Assert.All(result.Statements, stmt =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, stmt.Status));
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[2].Certainty);
    }

    [Fact]
    public void MissingIndexOnProvenExistingTableIsExactNoOp()
    {
        var result = Analyze(
            "CREATE TABLE orders (id INT);" +
            "DROP INDEX IF EXISTS ix_absent ON orders;" +
            "DROP INDEX IF EXISTS ix_absent ON orders;",
            profile: Version(16));

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, stmt =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, stmt.Status));
        Assert.Empty(result.Statements[1].Changes);
        Assert.Empty(result.Statements[2].Changes);
        Assert.Same(result.Statements[1].CatalogBefore, result.Statements[1].CatalogAfter);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void IndexOnAnotherTableMustNotBeDeleted()
    {
        const string sql =
            "CREATE TABLE orders (id INT);" +
            "CREATE TABLE events (id INT);" +
            "CREATE INDEX ix_shared ON orders(id);" +
            "DROP INDEX IF EXISTS ix_shared ON events;";
        var result = Analyze(sql, profile: Version(16));

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, stmt =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, stmt.Status));
        Assert.Empty(result.Statements[3].Changes);
        Assert.Equal("IX_SHARED",
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "ORDERS").Indexes).Name);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "EVENTS").Indexes);
    }

    [Fact]
    public void DropThenConditionalDropIsIdempotentOnSameTable()
    {
        const string sql =
            "CREATE TABLE orders (id INT);" +
            "CREATE INDEX ix_id ON orders(id);" +
            "DROP INDEX IF EXISTS ix_id ON orders;" +
            "DROP INDEX IF EXISTS ix_id ON orders;";
        var result = Analyze(sql, profile: Version(16));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(12)]
    public void UnprovenOrTooOldSqlServerVersionRejectsConditionalSyntax(int? version)
    {
        var result = Analyze("CREATE TABLE orders (id INT);" +
            "DROP INDEX IF EXISTS ix_id ON orders;",
            profile: version is null ? null : Version(version.Value));

        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, result.Statements[1].Certainty);
        Assert.Empty(Assert.Single(result.Statements[0].CatalogAfter.Tables).Indexes);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP INDEX IF EXISTS ix_id;")]
    [InlineData("DROP INDEX IF EXISTS dbo.ix_id ON orders;")]
    [InlineData("DROP INDEX IF EXISTS ix_id ON orders WITH (ONLINE = ON);")]
    [InlineData("DROP INDEX IF EXISTS ix_id ON orders, ix_other ON orders;")]
    [InlineData("DROP INDEX IF EXISTS ix_id ON orders CASCADE;")]
    public void UnsupportedSqlServerIndexDropShapesNeverMintCertainCatalog(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id INT);" + ddl, profile: Version(16));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void MissingTargetTableNeverCountsAsNoOp()
    {
        var result = Analyze(
            "DROP INDEX IF EXISTS ix_missing ON missing_table;",
            profile: Version(16));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ImplicitConstraintIndexNameCannotBeProvedAbsent()
    {
        var result = Analyze(
            "CREATE TABLE orders (id INT, CONSTRAINT pk_orders PRIMARY KEY (id));" +
            "DROP INDEX IF EXISTS pk_orders ON orders;",
            profile: Version(16));
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnknownCatalogCannotProveConditionalNoOp()
    {
        var result = Analyze("DROP INDEX IF EXISTS ix_unknown ON orders;",
            SqlCatalogState.Unknown(), Version(16));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsCannotInheritSqlServerConditionalOnTableSyntax(
        SqlAgentToolType provider)
    {
        var sql = "CREATE TABLE orders (id INT);" +
            "DROP INDEX IF EXISTS ix_missing ON orders;";
        var result = SqlCoreFacade.AnalyzeScript(sql, provider,
            SqlCatalogState.KnownEmpty(),
            new SqlProviderCapabilityProfile(provider, new Version(16, 0)));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
    }
}
