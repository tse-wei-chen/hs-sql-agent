using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Index namespace proofs are per-table on MySQL and SQL Server.
/// Other providers keep their catalog-wide conservative namespace until
/// a narrower schema-scoped namespace can be proved.
/// </summary>
public sealed class SqlCoreTableLocalIndexNamespaceTests
{
    private static SqlScriptAnalysisResult Analyze(SqlAgentToolType provider,
        string sql, SqlCatalogState? catalog = null) =>
        SqlCoreFacade.AnalyzeScript(sql, provider,
            catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    public void ReusingIndexNameOnDifferentTablesIsAnExactKnownDelta(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE alpha(id {typeName});" +
            $"CREATE TABLE beta(id {typeName});" +
            "CREATE INDEX ix_shared ON alpha(id);" +
            "CREATE INDEX ix_shared ON beta(id);");

        Assert.Equal(4, result.Statements.Count);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
        {
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
            Assert.Equal(SqlCatalogKnowledge.Known, statement.Certainty);
            Assert.Equal(SqlEffectCoverage.Complete, statement.EffectCoverage);
        });
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Equal(SqlCatalogKnowledge.Known,
            Assert.Single(result.Statements[3].DetailedEffects).Certainty);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(2, result.FinalCatalog.Tables.Count);
        Assert.All(result.FinalCatalog.Tables, table =>
        {
            Assert.Equal(SqlCatalogKnowledge.Known, table.Knowledge);
            Assert.Single(table.Indexes);
            Assert.Equal("IX_SHARED", table.Indexes[0].Name, ignoreCase: true);
        });
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    public void DropIndexOnOneOwnerDoesNotEraseIdenticallyNamedIndexOnOther(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE alpha(id {typeName});" +
            $"CREATE TABLE beta(id {typeName});" +
            "CREATE INDEX ix_shared ON alpha(id);" +
            "CREATE INDEX ix_shared ON beta(id);" +
            "DROP INDEX ix_shared ON alpha;");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[4].Status);
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(result.Statements[4].Changes).Operation);
        Assert.Empty(result.FinalCatalog.Tables.Single(table =>
            table.Name.Equals("alpha", StringComparison.OrdinalIgnoreCase)).Indexes);
        Assert.Single(result.FinalCatalog.Tables.Single(table =>
            table.Name.Equals("beta", StringComparison.OrdinalIgnoreCase)).Indexes);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    public void KeyConstraintOnUnrelatedTableDoesNotPoisonTargetIndexNamespace(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE parent(id {typeName} NOT NULL PRIMARY KEY);" +
            $"CREATE TABLE child(id {typeName});" +
            "CREATE INDEX ix_child_id ON child(id);");

        var statement = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Known, statement.Certainty);
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(statement.Changes).Operation);
        Assert.Single(result.FinalCatalog.Tables.Single(table =>
            table.Name.Equals("child", StringComparison.OrdinalIgnoreCase)).Indexes);
        Assert.Single(result.FinalCatalog.Tables.Single(table =>
            table.Name.Equals("parent", StringComparison.OrdinalIgnoreCase)).Constraints);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void GlobalIndexNamespaceStillRejectsDuplicateAcrossTables(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE alpha(id {typeName});" +
            $"CREATE TABLE beta(id {typeName});" +
            "CREATE INDEX ix_shared ON alpha(id);" +
            "CREATE INDEX ix_shared ON beta(id);");

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[3].Status);
        Assert.Empty(result.Statements[3].Changes);
        Assert.Same(result.Statements[3].CatalogBefore,
            result.Statements[3].CatalogAfter);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void OtherTableConstraintStillConservativelyBlocksGlobalIndexNamespace(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE parent(id {typeName} NOT NULL PRIMARY KEY);" +
            $"CREATE TABLE child(id {typeName});" +
            "CREATE INDEX ix_child_id ON child(id);");

        var statement = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, statement.Certainty);
        Assert.Empty(result.FinalCatalog.Tables.SelectMany(table => table.Indexes));
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Contains(statement.Diagnostics,
            diagnostic => diagnostic.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    public void ConstraintOnTargetTableStillRequiresUnprovenImplicitIndexProof(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE alpha(id {typeName} NOT NULL PRIMARY KEY);" +
            "CREATE INDEX ix_new ON alpha(id);");

        var statement = result.Statements[1];
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, statement.Certainty);
        Assert.Empty(statement.Changes.Where(change =>
            change.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER")]
    [InlineData(SqlAgentToolType.MySQL, "INT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER")]
    public void OpenWorldCatalogNeverMintsIndexOrDependencyProof(
        SqlAgentToolType provider, string typeName)
    {
        var result = Analyze(provider,
            $"CREATE TABLE alpha(id {typeName});" +
            $"CREATE TABLE beta(id {typeName});" +
            "CREATE INDEX ix_shared ON alpha(id);",
            SqlCatalogState.Unknown());

        var statement = result.Statements[2];
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, statement.Status);
        Assert.Equal(SqlCatalogKnowledge.Unknown, statement.Certainty);
        Assert.Empty(statement.Changes.Where(change =>
            change.Certainty == SqlCatalogKnowledge.Known));
        Assert.False(statement.CatalogAfter.IsClosedWorld);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.DoesNotContain(result.FinalCatalog.Tables,
            table => table.Knowledge == SqlCatalogKnowledge.Known);
    }
}
