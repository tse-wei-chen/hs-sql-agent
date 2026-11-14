using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQLite CREATE INDEX IF NOT EXISTS is a schema-wide index-name check, not
/// a table-local index signature check. Static proof must match real SQLite.
/// </summary>
public sealed class SqlCoreSqliteConditionalCreateIndexTests
{
    private const string Tables =
        "CREATE TABLE orders (id INTEGER, state TEXT);" +
        "CREATE TABLE events (id INTEGER, state TEXT);";

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType source = SqlAgentToolType.Sqlite) =>
        SqlCoreFacade.AnalyzeScript(sql, source, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON orders(state);")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON orders(id,state);")]
    public void FirstConditionalCreateProvesExactlyOneIndexDelta(string indexSql)
    {
        var result = Analyze(Tables + indexSql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[2].Certainty);
        Assert.Equal(SqlEffectCoverage.Complete, result.Statements[2].EffectCoverage);
        Assert.Equal(SqlCatalogOperation.CreateIndex, Assert.Single(result.Statements[2].Changes).Operation);
        var owner = result.FinalCatalog.Tables.Single(t => t.Name == "ORDERS");
        var index = Assert.Single(owner.Indexes);
        Assert.Equal("IX_STATE", index.Name);
        Assert.Equal(indexSql.Contains("id,state", StringComparison.Ordinal)
            ? new[] { "ID", "STATE" } : new[] { "STATE" }, index.Columns);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "EVENTS").Indexes);
    }

    [Theory]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON orders(id);")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON events(state);")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON events(missing_column);")]
    public void ExistingIndexOnEitherTableIsExactNoOpEvenWhenColumnsDiffer(string repeatedSql)
    {
        var sql = Tables +
            "CREATE INDEX ix_state ON orders(state);" + repeatedSql;
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        var noOp = result.Statements[3];
        Assert.Empty(noOp.Changes);
        Assert.Same(noOp.CatalogBefore, noOp.CatalogAfter);
        var owner = result.FinalCatalog.Tables.Single(t => t.Name == "ORDERS");
        Assert.Equal(new[] { "STATE" }, Assert.Single(owner.Indexes).Columns);
        Assert.Empty(result.FinalCatalog.Tables.Single(t => t.Name == "EVENTS").Indexes);
    }

    [Fact]
    public void DropThenConditionalRecreateUpdatesIndexLineage()
    {
        const string sql =
            "CREATE TABLE orders (id INTEGER, state TEXT);" +
            "CREATE INDEX ix_state ON orders(state);" +
            "DROP INDEX ix_state;" +
            "CREATE INDEX IF NOT EXISTS ix_state ON orders(id);";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, stmt =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, stmt.Status));
        Assert.Equal(SqlCatalogOperation.DropIndex, Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal(SqlCatalogOperation.CreateIndex, Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Equal(new[] { "ID" },
            Assert.Single(Assert.Single(result.FinalCatalog.Tables).Indexes).Columns);
    }

    [Theory]
    [InlineData("CREATE INDEX IF NOT EXISTS orders ON events(state);")]
    [InlineData("CREATE INDEX IF NOT EXISTS events ON orders(id);")]
    public void IndexNameCollidingWithKnownTableCannotBecomeKnown(string indexSql)
    {
        var result = Analyze(Tables + indexSql);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            delta => delta.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Tables.SelectMany(t => t.Indexes));
    }

    [Theory]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_missing ON orders(missing);")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON missing(id);")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON orders(state) WHERE id > 0;")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON orders(lower(state));")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_state ON orders(state DESC);")]
    [InlineData("CREATE UNIQUE INDEX IF NOT EXISTS ix_state ON orders(state);")]
    public void UnsupportedOrUnprovenConditionalIndexCannotPublishKnownDelta(string sql)
    {
        var result = Analyze(Tables + sql);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            delta => delta.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void ExistingNameCannotBypassMissingTargetTableProof()
    {
        var result = Analyze(Tables +
            "CREATE INDEX ix_state ON orders(state);" +
            "CREATE INDEX IF NOT EXISTS ix_state ON absent(id);");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            delta => delta.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnknownCatalogCannotProveIndexPresenceOrAbsence()
    {
        var result = Analyze("CREATE INDEX IF NOT EXISTS ix_state ON orders(state);",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherFiveDialectsDoNotInheritSqliteConditionalIndexProof(SqlAgentToolType provider)
    {
        var result = Analyze("CREATE INDEX IF NOT EXISTS ix_state ON orders(state);",
            source: provider);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void LiveSqliteIndexNameNoOpAndWrongKindCollisionMatchStaticCatalog()
    {
        const string sql = Tables +
            "CREATE INDEX IF NOT EXISTS ix_state ON orders(state);" +
            "CREATE INDEX IF NOT EXISTS ix_state ON events(missing_column);" +
            "DROP INDEX ix_state;" +
            "CREATE INDEX IF NOT EXISTS ix_state ON events(id);";
        var analyzed = Analyze(sql);
        Assert.Empty(analyzed.Diagnostics);
        Assert.All(analyzed.Statements, stmt =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, stmt.Status));

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        for (var i = 0; i < analyzed.Statements.Count; i++)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = analyzed.Statements[i].Syntax.Text;
            cmd.ExecuteNonQuery();

            using var names = db.CreateCommand();
            names.CommandText =
                "SELECT tbl_name, name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_autoindex_%' ORDER BY name";
            using var reader = names.ExecuteReader();
            var live = new List<(string Owner, string Index)>();
            while (reader.Read())
                live.Add((reader.GetString(0).ToUpperInvariant(),
                    reader.GetString(1).ToUpperInvariant()));
            var expected = analyzed.Statements[i].CatalogAfter.Tables
                .SelectMany(table => table.Indexes.Select(index =>
                    (Owner: table.Name, Index: index.Name)))
                .OrderBy(pair => pair.Index, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected, live.OrderBy(pair => pair.Index, StringComparer.Ordinal));
        }

        using var collision = db.CreateCommand();
        collision.CommandText = "CREATE INDEX IF NOT EXISTS orders ON events(id);";
        Assert.Throws<SqliteException>(() => collision.ExecuteNonQuery());
        var invalid = Analyze(sql + collision.CommandText);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, invalid.Statements[^1].Status);
    }
}
