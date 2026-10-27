using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real Oracle Database Free 23/26ai DDL parity. Analyzer has no connection:
/// the harness runs each statement after AnalyzeScript and observes USER_*.
/// DDL implicitly commits in Oracle; isolation requires unique object names
/// and reliable explicit cleanup, NOT an enclosing rollback.
/// </summary>
public sealed class SqlCoreOracleLiveDdlParityTests
{
    private sealed record Column(string Name, string Type, bool Nullable);
    private sealed record Table(string Name, Column[] Columns, string[] Indexes);
    private sealed record Scenario(string Name, string Template);

    private static readonly Scenario[] Scenarios =
    [
        new("create_add", "CREATE TABLE {T1}(ID NUMBER NOT NULL, CODE VARCHAR2(32)); ALTER TABLE {T1} ADD (QTY NUMBER);"),
        new("create_drop_column", "CREATE TABLE {T1}(ID NUMBER, NOTE VARCHAR2(32)); ALTER TABLE {T1} DROP COLUMN NOTE;"),
        new("create_add_drop", "CREATE TABLE {T1}(ID NUMBER); ALTER TABLE {T1} ADD (STATE VARCHAR2(32)); ALTER TABLE {T1} DROP COLUMN STATE;"),
        new("two_tables", "CREATE TABLE {T1}(ID NUMBER); CREATE TABLE {T2}(ID NUMBER, PARENT_ID NUMBER);"),
        new("drop_secondary", "CREATE TABLE {T1}(ID NUMBER); CREATE TABLE {T2}(ID NUMBER); DROP TABLE {T2};"),
        new("drop_recreate", "CREATE TABLE {T1}(ID NUMBER); DROP TABLE {T1}; CREATE TABLE {T1}(CODE VARCHAR2(32) NOT NULL);"),
        new("index_kept", "CREATE TABLE {T1}(ID NUMBER, STATE VARCHAR2(32)); CREATE INDEX {I1} ON {T1}(STATE);"),
        new("index_removed", "CREATE TABLE {T1}(ID NUMBER, STATE VARCHAR2(32)); CREATE INDEX {I1} ON {T1}(STATE); DROP INDEX {I1};"),
        new("two_indexes", "CREATE TABLE {T1}(ID NUMBER, STATE VARCHAR2(32), TAG VARCHAR2(32)); CREATE INDEX {I1} ON {T1}(STATE); CREATE INDEX {I2} ON {T1}(TAG);"),
        new("index_table_drop", "CREATE TABLE {T1}(ID NUMBER, LABEL VARCHAR2(32)); CREATE INDEX {I1} ON {T1}(LABEL); DROP TABLE {T1};"),
        new("add_then_index", "CREATE TABLE {T1}(ID NUMBER); ALTER TABLE {T1} ADD (LABEL VARCHAR2(32)); CREATE INDEX {I1} ON {T1}(LABEL);"),
        new("index_drop_then_column", "CREATE TABLE {T1}(ID NUMBER, LABEL VARCHAR2(32)); CREATE INDEX {I1} ON {T1}(LABEL); DROP INDEX {I1}; ALTER TABLE {T1} DROP COLUMN LABEL;"),
        new("create_drop_other", "CREATE TABLE {T1}(ID NUMBER); CREATE TABLE {T2}(ID NUMBER, CODE VARCHAR2(32)); ALTER TABLE {T2} ADD (QTY NUMBER); DROP TABLE {T1};"),
        new("nullable_columns", "CREATE TABLE {T1}(ID NUMBER NOT NULL, LABEL VARCHAR2(32)); ALTER TABLE {T1} ADD (QTY NUMBER);"),
        new("quoted_simple", "CREATE TABLE \"{T1}\"(\"ID\" NUMBER NOT NULL, \"LABEL\" VARCHAR2(32)); ALTER TABLE \"{T1}\" ADD (\"QTY\" NUMBER);"),
        new("explicit_rollback_after_ddl", "CREATE TABLE {T1}(ID NUMBER); ROLLBACK;"),
        new("explicit_commit_after_ddl", "CREATE TABLE {T1}(ID NUMBER); COMMIT;")
    ];

    public static IEnumerable<object[]> Cases => Scenarios.Select(x => new object[] { x.Name });

    [Fact]
    public void CorpusContainsSeventeenDistinctMultiStatementOracleWorkflows()
    {
        Assert.Equal(17, Scenarios.Length);
        Assert.Equal(17, Scenarios.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(17, Scenarios.Select(x => x.Template).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Scenarios, s => Assert.True(s.Template.Count(x => x == ';') >= 2, s.Name));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryProvenOracleCatalogTransitionMatchesActualOracleMetadata(string id)
    {
        var source = ConnectionString();
        var scenario = Scenarios.Single(x => x.Name == id);
        // Database-wide object namespace in Oracle requires unique, generated
        // names; no test user input participates in SQL name generation.
        var prefix = "HSA6_" + Guid.NewGuid().ToString("N")[..14].ToUpperInvariant();
        var sql = scenario.Template
            .Replace("{T1}", prefix + "_T1", StringComparison.Ordinal)
            .Replace("{T2}", prefix + "_T2", StringComparison.Ordinal)
            .Replace("{I1}", prefix + "_I1", StringComparison.Ordinal)
            .Replace("{I2}", prefix + "_I2", StringComparison.Ordinal);

        // Complete static analysis BEFORE the execution connection is opened.
        var analysis = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty());
        Assert.True(analysis.Statements.Count >= 2, id);
        Assert.All(analysis.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        Assert.Empty(analysis.Diagnostics);

        await using var db = new OracleConnection(source);
        await OpenWhenReady(db);
        try
        {
            for (var i = 0; i < analysis.Statements.Count; i++)
            {
                var statement = analysis.Statements[i];
                Assert.True(statement.CatalogAfter.IsClosedWorld, $"{id}#{i}");
                await Execute(db, statement.Syntax.Text);
                AssertEquivalent(id, i, statement.CatalogAfter, ReadSchema(db, prefix));
            }
            AssertEquivalent(id, analysis.Statements.Count,
                analysis.FinalCatalog, ReadSchema(db, prefix));
        }
        finally
        {
            // Oracle DDL auto-commits. Explicit teardown is essential even
            // after an assertion failure or a partially successful script.
            foreach (var table in ReadSchema(db, prefix)
                         .OrderByDescending(x => x.Name, StringComparer.Ordinal))
                await Execute(db, "DROP TABLE \"" +
                    table.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" PURGE");
            Assert.Empty(ReadSchema(db, prefix));
        }
    }

    [Fact]
    public async Task RealOracleDdlImplicitlyCommitsPriorDmlDespiteSubsequentRollback()
    {
        var prefix = "HSA6_" + Guid.NewGuid().ToString("N")[..14].ToUpperInvariant();
        var original = prefix + "_T1";
        var second = prefix + "_T2";
        var script = $"CREATE TABLE {original}(ID NUMBER);" +
                     $"INSERT INTO {original}(ID) VALUES (7);" +
                     $"CREATE TABLE {second}(ID NUMBER);" +
                     "ROLLBACK;";
        var modeled = SqlCoreFacade.AnalyzeScript(
            script, SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            modeled.Statements[2].TransactionProvenance.Origin);
        Assert.True(modeled.Statements[2].Effects.HasFlag(SqlEffectKind.ChangeTransaction));
        Assert.False(modeled.Statements[2].TransactionProvenance.IsServerRollbackProven);
        Assert.True(modeled.FinalCatalog.IsClosedWorld);
        Assert.Equal(2, modeled.FinalCatalog.Tables.Count);

        await using var db = new OracleConnection(ConnectionString());
        await OpenWhenReady(db);
        try
        {
            foreach (var item in modeled.Statements)
                await Execute(db, item.Syntax.Text);
            AssertEquivalent("implicit-commit", modeled.Statements.Count,
                modeled.FinalCatalog, ReadSchema(db, prefix));
            // DDL commits before its own execution. The preceding INSERT must
            // survive a subsequent ROLLBACK in the actual Oracle engine.
            using var cmd = new OracleCommand("SELECT COUNT(*) FROM " + original +
                " WHERE ID = 7", db);
            Assert.Equal(1, Convert.ToInt32(
                await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            foreach (var table in ReadSchema(db, prefix)
                         .OrderByDescending(x => x.Name, StringComparer.Ordinal))
                await Execute(db, "DROP TABLE \"" +
                    table.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" PURGE");
        }
    }

    [Fact]
    public void CheckConstraintThatOracleSupportsRemainsUnprovenWhenAnalyzerDoesNotModelIt()
    {
        var report = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE x_check(ID NUMBER, CHECK (ID > 0));",
            SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            Assert.Single(report.Statements).Status);
        Assert.Equal(SqlEffectCoverage.Unknown, report.EffectCoverage);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Real Oracle DDL parity requires TEST_ORACLE_CONNECTION in CI.");
        Assert.Skip("Real Oracle DDL parity requires TEST_ORACLE_CONNECTION.");
        throw new InvalidOperationException("Skipped.");
    }

    private static async Task OpenWhenReady(OracleConnection db)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var retry = 0; ; retry++)
        {
            try
            {
                await db.OpenAsync(ct);
                break;
            }
            catch (OracleException) when (retry < 60 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private static async Task Execute(OracleConnection db, string sql)
    {
        using var command = new OracleCommand(sql, db);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static IReadOnlyList<Table> ReadSchema(OracleConnection db, string prefix)
    {
        var names = new List<string>();
        using (var cmd = new OracleCommand(
            "SELECT TABLE_NAME FROM USER_TABLES ORDER BY TABLE_NAME", db))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (name.StartsWith(prefix + "_", StringComparison.Ordinal))
                    names.Add(name);
            }

        var result = new List<Table>();
        foreach (var name in names)
        {
            var columns = new List<Column>();
            using (var cmd = new OracleCommand(
                "SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, NULLABLE " +
                "FROM USER_TAB_COLUMNS WHERE TABLE_NAME = :tableName ORDER BY COLUMN_ID", db))
            {
                cmd.Parameters.Add("tableName", OracleDbType.Varchar2).Value = name;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var nativeType = reader.GetString(1);
                    var length = Convert.ToInt32(reader.GetValue(2));
                    var type = nativeType == "VARCHAR2"
                        ? "VARCHAR2(" + length + ")" : nativeType;
                    columns.Add(new Column(
                        reader.GetString(0), type, reader.GetString(3) == "Y"));
                }
            }

            var indexes = new List<string>();
            using (var cmd = new OracleCommand(
                "SELECT INDEX_NAME FROM USER_INDEXES " +
                "WHERE TABLE_NAME = :tableName ORDER BY INDEX_NAME", db))
            {
                cmd.Parameters.Add("tableName", OracleDbType.Varchar2).Value = name;
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) indexes.Add(reader.GetString(0));
            }
            result.Add(new Table(name, columns.ToArray(), indexes.ToArray()));
        }
        return result;
    }

    private static void AssertEquivalent(string scenario, int statementIndex,
        SqlCatalogState model, IReadOnlyList<Table> live)
    {
        Assert.True(model.IsClosedWorld, $"{scenario}#{statementIndex}: unknown catalog");
        Assert.Equal(model.Tables.Count, live.Count);
        foreach (var table in model.Tables)
        {
            Assert.Equal(SqlCatalogKnowledge.Known, table.Knowledge);
            var physical = Assert.Single(live, x => string.Equals(
                x.Name, table.Name, StringComparison.Ordinal));
            Assert.Equal(table.Columns.Count, physical.Columns.Length);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var expected = table.Columns[i];
                var actual = physical.Columns[i];
                Assert.Equal(expected.Name, actual.Name);
                Assert.True(string.Equals(expected.TypeName, actual.Type,
                    StringComparison.OrdinalIgnoreCase),
                    $"{scenario}#{statementIndex}/{table.Name}.{expected.Name}: " +
                    $"{expected.TypeName} != {actual.Type}");
                Assert.Equal(expected.Nullable, actual.Nullable);
            }
            Assert.Equal(table.Indexes.Select(x => x.Name).Order(StringComparer.Ordinal),
                physical.Indexes.Order(StringComparer.Ordinal));
        }
    }
}
