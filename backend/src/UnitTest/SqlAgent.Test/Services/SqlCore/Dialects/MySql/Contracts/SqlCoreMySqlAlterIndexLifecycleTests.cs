using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// MySQL ALTER TABLE ADD/DROP INDEX|KEY is an alternative syntax for
/// the existing typed secondary-index lifecycle, never a new SQL executor.
/// </summary>
public sealed class SqlCoreMySqlAlterIndexLifecycleTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType provider = SqlAgentToolType.MySQL) =>
        SqlCoreFacade.AnalyzeScript(sql, provider,
            catalog ?? SqlCatalogState.KnownEmpty(), null,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("ADD INDEX")]
    [InlineData("ADD KEY")]
    public void AlterAddSecondaryIndexIsExactKnownDelta(string keyword)
    {
        var script = "CREATE TABLE orders (id INT, state VARCHAR(64));" +
                     $"ALTER TABLE orders {keyword} ix_state (state, id);";
        var result = Analyze(script);

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, item =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, item.Status));
        Assert.Equal(SqlCatalogOperation.CreateIndex,
            Assert.Single(result.Statements[1].Changes).Operation);
        var index = Assert.Single(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.Equal("ix_state", index.Name);
        Assert.Equal(new[] { "state", "id" }, index.Columns.ToArray());
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[1].Certainty);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("DROP INDEX")]
    [InlineData("DROP KEY")]
    public void AlterDropSecondaryIndexIsExactKnownDelta(string keyword)
    {
        var result = Analyze(
            "CREATE TABLE orders (id INT, state VARCHAR(64));" +
            "CREATE INDEX ix_state ON orders(state);" +
            $"ALTER TABLE orders {keyword} ix_state;");

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, item =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, item.Status));
        Assert.Equal(SqlCatalogOperation.DropIndex,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Indexes);
        Assert.Single(Assert.Single(result.Statements[1].CatalogAfter.Tables).Indexes);
    }

    [Fact]
    public void BothSqlEntryPointsShareOneIndexCatalogWithImmutableHistory()
    {
        const string sql =
            "CREATE TABLE orders (id INT, state VARCHAR(64));" +
            "ALTER TABLE orders ADD KEY ix_one (id);" +
            "DROP INDEX ix_one ON orders;" +
            "CREATE INDEX ix_two ON orders(state);" +
            "ALTER TABLE orders DROP KEY ix_two;" +
            "ALTER TABLE orders ADD INDEX ix_three (state);";
        var result = Analyze(sql);

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, item =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, item.Status));
        Assert.Equal(
            new[] { SqlCatalogOperation.CreateIndex, SqlCatalogOperation.DropIndex,
                SqlCatalogOperation.CreateIndex, SqlCatalogOperation.DropIndex,
                SqlCatalogOperation.CreateIndex },
            result.Statements.Skip(1).Select(s => Assert.Single(s.Changes).Operation).ToArray());
        Assert.Equal("ix_one",
            Assert.Single(Assert.Single(result.Statements[1].CatalogAfter.Tables).Indexes).Name);
        Assert.Equal("ix_three",
            Assert.Single(Assert.Single(result.FinalCatalog.Tables).Indexes).Name);
    }

    [Fact]
    public void IndexNameIsScopedToOwningTableNotEntireDatabase()
    {
        const string sql =
            "CREATE TABLE orders (id INT);" +
            "CREATE TABLE events (id INT);" +
            "ALTER TABLE orders ADD INDEX ix_shared (id);" +
            "ALTER TABLE events ADD KEY ix_shared (id);" +
            "ALTER TABLE events DROP INDEX ix_shared;";
        var result = Analyze(sql);

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, item =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, item.Status));
        Assert.Equal("ix_shared",
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "orders").Indexes).Name);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "events").Indexes);
    }

    [Fact]
    public void RemovingIndexUnlocksDropColumnWithoutMutatingEarlierSnapshots()
    {
        const string sql =
            "CREATE TABLE orders (id INT, note VARCHAR(64));" +
            "ALTER TABLE orders ADD INDEX ix_note (note);" +
            "ALTER TABLE orders DROP INDEX ix_note;" +
            "ALTER TABLE orders DROP COLUMN note;";
        var result = Analyze(sql);

        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, item =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, item.Status));
        Assert.Equal(SqlCatalogOperation.DropColumn,
            Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Single(Assert.Single(result.FinalCatalog.Tables).Columns);
        Assert.Single(Assert.Single(result.Statements[1].CatalogAfter.Tables).Indexes);
        Assert.Empty(Assert.Single(result.Statements[2].CatalogAfter.Tables).Indexes);
    }

    [Theory]
    [InlineData("ALTER TABLE orders ADD INDEX ix_bad (missing);")]
    [InlineData("ALTER TABLE orders ADD INDEX ix_bad (id, id);")]
    [InlineData("ALTER TABLE orders ADD KEY ix_bad (id DESC);")]
    [InlineData("ALTER TABLE orders ADD INDEX ix_bad (id(4));")]
    [InlineData("ALTER TABLE orders ADD INDEX ix_bad ((id + 1));")]
    [InlineData("ALTER TABLE orders ADD UNIQUE INDEX ix_bad (id);")]
    [InlineData("ALTER TABLE orders ADD INDEX (id);")]
    [InlineData("ALTER TABLE orders DROP PRIMARY KEY;")]
    [InlineData("ALTER TABLE orders ADD INDEX ix_bad (id), DROP INDEX ix_bad;")]
    [InlineData("ALTER TABLE orders ADD INDEX ix_bad (id) ALGORITHM=INPLACE;")]
    public void UnprovenOrUnsafeAlterIndexFormsCannotMintKnownIndex(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id INT);" + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Empty(Assert.Single(result.Statements[0].CatalogAfter.Tables).Indexes);
        Assert.DoesNotContain(result.Statements[1].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("ALTER TABLE orders ADD INDEX ix_id (id);")]
    [InlineData("ALTER TABLE orders DROP INDEX ix_id;")]
    public void UnknownInitialCatalogCannotProveNewIndexEvidence(string ddl)
    {
        var result = Analyze(ddl, SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("ALTER TABLE absent ADD INDEX ix_id (id);")]
    [InlineData("ALTER TABLE absent DROP INDEX ix_id;")]
    [InlineData("ALTER TABLE orders DROP INDEX ix_missing;")]
    public void MissingTableOrMissingIndexCannotProduceExactChange(string ddl)
    {
        var result = Analyze("CREATE TABLE orders (id INT);" + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Empty(Assert.Single(result.Statements[0].CatalogAfter.Tables).Indexes);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void MySqlAlterIndexGrammarNeverLeaksToOtherDialects(SqlAgentToolType provider)
    {
        var result = Analyze("ALTER TABLE orders ADD INDEX ix_id (id);", provider: provider);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
    }

    [Fact]
    public void ImplicitPrimaryKeyIndexCannotBeErasedUsingDropIndex()
    {
        const string script =
            "CREATE TABLE orders (id INT NOT NULL, CONSTRAINT pk_orders PRIMARY KEY (id));" +
            "ALTER TABLE orders DROP INDEX PRIMARY;";
        var result = Analyze(script);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey,
            Assert.Single(Assert.Single(result.Statements[0].CatalogAfter.Tables).Constraints).Kind);
    }
}
