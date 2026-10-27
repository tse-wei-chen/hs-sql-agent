using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// View output aliases must never erase the physical source-column dependency.
/// This is a static Catalog proof, not authorization to execute a view definition.
/// </summary>
public sealed class SqlCorePostgresViewColumnAliasTests
{
    private const string Base =
        "CREATE TABLE app.orders(id INTEGER NOT NULL, amount INTEGER, unused INTEGER);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType provider = SqlAgentToolType.Postgres,
        SqlCatalogState? initial = null) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, initial ?? SqlCatalogState.KnownEmpty());

    [Fact]
    public void AliasesProjectTypedOutputColumnsWithoutLosingSourceTypes()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v_orders AS SELECT id AS order_id, amount AS total FROM app.orders;");
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements,
            statement => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        var table = Assert.Single(result.FinalCatalog.Tables);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "order_id", "total" },
            view.Columns.Select(column => column.Name).ToArray());
        Assert.Equal(table.Columns[0].TypeName, view.Columns[0].TypeName);
        Assert.Equal(table.Columns[0].Nullable, view.Columns[0].Nullable);
        Assert.Equal(table.Columns[1].TypeName, view.Columns[1].TypeName);
        Assert.Equal("app.orders", Assert.Single(view.Dependencies).DisplayName);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DropSourceColumnReferencedOnlyByAliasRemainsBlocked()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v_orders AS SELECT id AS order_id FROM app.orders;" +
            "ALTER TABLE app.orders DROP COLUMN id;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        var drop = result.Statements[2];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, drop.Status);
        Assert.Empty(drop.Changes);
        Assert.Same(drop.CatalogBefore, drop.CatalogAfter);
        Assert.Equal(3, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
        Assert.Single(result.FinalCatalog.Views);
    }

    [Fact]
    public void UnreferencedSourceColumnCanStillBeDropped()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v_orders AS SELECT id AS order_id FROM app.orders;" +
            "ALTER TABLE app.orders DROP COLUMN unused;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(2, Assert.Single(result.FinalCatalog.Tables).Columns.Count);
        Assert.Equal("order_id", Assert.Single(Assert.Single(result.FinalCatalog.Views).Columns).Name);
    }

    [Fact]
    public void RepeatedPhysicalColumnWithDistinctOutputAliasesIsProven()
    {
        var result = Analyze(Base +
            "CREATE VIEW app.v_orders AS SELECT id AS first_id, id AS second_id FROM app.orders;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "first_id", "second_id" },
            Assert.Single(result.FinalCatalog.Views).Columns.Select(c => c.Name).ToArray());
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
    }

    [Fact]
    public void AliasedViewChainStillBlocksRestrictedDropAndSupportsCascade()
    {
        const string views =
            "CREATE VIEW app.v_orders AS SELECT id AS order_id FROM app.orders;" +
            "CREATE VIEW app.v_child AS SELECT order_id AS child_id FROM app.v_orders;";
        var restricted = Analyze(Base + views + "DROP TABLE app.orders RESTRICT;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, restricted.Statements[3].Status);
        Assert.Equal(2, restricted.FinalCatalog.Views.Count);
        var cascade = Analyze(Base + views + "DROP TABLE app.orders CASCADE;");
        Assert.Empty(cascade.Diagnostics);
        Assert.Equal(3, cascade.Statements[3].Changes.Count);
        Assert.Empty(cascade.FinalCatalog.Tables);
        Assert.Empty(cascade.FinalCatalog.Views);
    }

    [Theory]
    [InlineData("SELECT id AS duplicate, amount AS duplicate FROM app.orders")]
    [InlineData("SELECT absent AS renamed FROM app.orders")]
    public void InvalidAliasProofDoesNotPublishKnownView(string query)
    {
        var result = Analyze(Base + "CREATE VIEW app.v_orders AS " + query + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Empty(result.FinalCatalog.Views);
        Assert.Empty(result.Statements[1].Changes.Where(
            change => change.Certainty == SqlCatalogKnowledge.Known));
    }

    [Theory]
    [InlineData("SELECT id order_id FROM app.orders")]
    [InlineData("SELECT id + 1 AS order_id FROM app.orders")]
    [InlineData("SELECT * AS everything FROM app.orders")]
    [InlineData("SELECT id AS order_id FROM app.orders WHERE id > 0")]
    public void OutsideProvenAliasGrammarRemainsUnsupported(string query)
    {
        var result = Analyze(Base + "CREATE VIEW app.v_orders AS " + query + ";");
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Empty(result.FinalCatalog.Views);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MissingSourceAndOpenWorldNeverMintAliasedView()
    {
        var missing = Analyze("CREATE VIEW app.v AS SELECT id AS alias_id FROM app.missing;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(missing.Statements).Status);
        Assert.Empty(missing.FinalCatalog.Views);
        var openWorld = Analyze("CREATE VIEW app.v AS SELECT id AS alias_id FROM app.orders;",
            initial: SqlCatalogState.Unknown());
        Assert.NotEqual(SqlCatalogKnowledge.Known, Assert.Single(openWorld.Statements).Certainty);
        Assert.Empty(openWorld.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsDoNotInheritPostgresAliasProof(SqlAgentToolType dialect)
    {
        var result = Analyze(
            "CREATE VIEW app.v AS SELECT id AS renamed FROM app.parent;",
            provider: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }
}
