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
/// Independent Oracle Free FK differential oracle. USER_CONSTRAINTS joins
/// parent/child USER_CONS_COLUMNS in POSITION order after each DDL.
/// Oracle DDL auto-commits: each run uses unique names and explicit teardown.
/// SqlCore itself never opens an execution connection.
/// </summary>
public sealed class SqlCoreOracleInboundForeignKeyLiveParityTests
{
    private sealed record ForeignKey(string Table, string Name, string Target,
        string[] LocalColumns, string[] ReferencedColumns);
    private sealed record Scenario(string Name, string Sql);
    private static readonly Scenario[] Scenarios =
    [
        new("single_inbound",
            "CREATE TABLE {T1}(ID NUMBER NOT NULL, CONSTRAINT {C1} PRIMARY KEY(ID));" +
            "CREATE TABLE {T2}(PID NUMBER, CONSTRAINT {C2} FOREIGN KEY(PID) REFERENCES {T1}(ID));" +
            "ALTER TABLE {T1} RENAME COLUMN ID TO NEW_ID;"),
        new("two_children_composite_chain",
            "CREATE TABLE {T1}(A NUMBER NOT NULL, B NUMBER NOT NULL, CONSTRAINT {C1} PRIMARY KEY(A,B));" +
            "CREATE TABLE {T2}(X NUMBER, Y NUMBER, CONSTRAINT {C2} FOREIGN KEY(X,Y) REFERENCES {T1}(A,B));" +
            "CREATE TABLE {T3}(M NUMBER, N NUMBER, CONSTRAINT {C3} FOREIGN KEY(M,N) REFERENCES {T1}(A,B));" +
            "ALTER TABLE {T1} RENAME COLUMN B TO MIDDLE_B;" +
            "ALTER TABLE {T1} RENAME COLUMN MIDDLE_B TO FINAL_B;"),
        new("self_fk_local_and_remote",
            "CREATE TABLE {T1}(ID NUMBER NOT NULL, PARENT_ID NUMBER, " +
            "CONSTRAINT {C1} PRIMARY KEY(ID), " +
            "CONSTRAINT {C2} FOREIGN KEY(PARENT_ID) REFERENCES {T1}(ID));" +
            "ALTER TABLE {T1} RENAME COLUMN ID TO NODE_ID;" +
            "ALTER TABLE {T1} RENAME COLUMN PARENT_ID TO ANCESTOR_ID;"),
        new("unrelated_parent_isolation",
            "CREATE TABLE {T1}(ID NUMBER NOT NULL, CONSTRAINT {C1} PRIMARY KEY(ID));" +
            "CREATE TABLE {T2}(ID NUMBER NOT NULL, CONSTRAINT {C2} PRIMARY KEY(ID));" +
            "CREATE TABLE {T3}(FIRST_ID NUMBER, OTHER_ID NUMBER, " +
            "CONSTRAINT {C3} FOREIGN KEY(FIRST_ID) REFERENCES {T1}(ID), " +
            "CONSTRAINT {C4} FOREIGN KEY(OTHER_ID) REFERENCES {T2}(ID));" +
            "ALTER TABLE {T1} RENAME COLUMN ID TO CODE;"),
        new("rename_local_then_remote",
            "CREATE TABLE {T1}(ID NUMBER NOT NULL, NOTE NUMBER, CONSTRAINT {C1} PRIMARY KEY(ID));" +
            "CREATE TABLE {T2}(PARENT_ID NUMBER, CONSTRAINT {C2} FOREIGN KEY(PARENT_ID) REFERENCES {T1}(ID));" +
            "ALTER TABLE {T2} RENAME COLUMN PARENT_ID TO PARENT_REF;" +
            "ALTER TABLE {T1} RENAME COLUMN ID TO CODE;")
    ];

    public static IEnumerable<object[]> Cases =>
        Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void FiveIndependentPhysicalOracleFkWorkflows()
    {
        Assert.Equal(5, Scenarios.Length);
        Assert.Equal(5, Scenarios.Select(x => x.Name).Distinct().Count());
        Assert.Equal(5, Scenarios.Select(x => x.Sql).Distinct().Count());
        Assert.All(Scenarios, x => Assert.True(x.Sql.Count(c => c == ';') >= 3));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryOracleDdlStatementPreservesPhysicalForeignKeyColumnLineage(string id)
    {
        var scenario = Scenarios.Single(s => s.Name == id);
        var prefix = "HSAFK_" + Guid.NewGuid().ToString("N")[..14].ToUpperInvariant();
        var sql = scenario.Sql;
        foreach (var suffix in new[] { "T1", "T2", "T3", "C1", "C2", "C3", "C4" })
            sql = sql.Replace("{" + suffix + "}", prefix + "_" + suffix,
                StringComparison.Ordinal);

        // Fail closed at analysis before touching the Oracle service.
        var analysis = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty(),
            null, TestContext.Current.CancellationToken);
        Assert.Empty(analysis.Diagnostics);
        Assert.All(analysis.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));

        await using var db = new OracleConnection(ConnectionString());
        await OpenWhenReady(db);
        try
        {
            for (var i = 0; i < analysis.Statements.Count; i++)
            {
                var step = analysis.Statements[i];
                Assert.True(step.CatalogAfter.IsClosedWorld, id + "#" + i);
                await Execute(db, step.Syntax.Text);
                var actual = ReadForeignKeys(db, prefix);
                var expected = step.CatalogAfter.Tables
                    .SelectMany(table => table.Constraints
                        .Where(c => c.Kind == SqlCatalogConstraintKind.ForeignKey)
                        .Select(c => new ForeignKey(
                            table.QualifiedName!.Parts[^1], c.Name,
                            c.ReferencedIdentity!.Parts[^1],
                            c.Columns.ToArray(), c.ReferencedColumns.ToArray())))
                    .OrderBy(fk => fk.Table, StringComparer.Ordinal)
                    .ThenBy(fk => fk.Name, StringComparer.Ordinal).ToArray();
                Assert.Equal(expected.Length, actual.Length);
                for (var j = 0; j < expected.Length; j++)
                {
                    Assert.Equal(expected[j].Table, actual[j].Table);
                    Assert.Equal(expected[j].Name, actual[j].Name);
                    Assert.Equal(expected[j].Target, actual[j].Target);
                    Assert.Equal(expected[j].LocalColumns, actual[j].LocalColumns);
                    Assert.Equal(expected[j].ReferencedColumns, actual[j].ReferencedColumns);
                }
            }
        }
        finally
        {
            // Oracle DDL commits automatically; rollback cannot clean up.
            // Cascade constraints prevents surviving cross-table FKs from
            // obstructing a deterministic failure-safe test teardown.
            foreach (var table in ReadTables(db, prefix))
                await Execute(db, "DROP TABLE \"" +
                    table.Replace("\"", "\"\"", StringComparison.Ordinal) +
                    "\" CASCADE CONSTRAINTS PURGE");
            Assert.Empty(ReadTables(db, prefix));
        }
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                          StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_ORACLE_CONNECTION is required in CI.");
        Assert.Skip("Requires a live Oracle service and TEST_ORACLE_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task OpenWhenReady(OracleConnection db)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(ct); return; }
            catch (OracleException) when (retry < 60 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
    }

    private static async Task Execute(OracleConnection db, string sql)
    {
        using var cmd = new OracleCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string[] ReadTables(OracleConnection db, string prefix)
    {
        var result = new List<string>();
        using var command = new OracleCommand("SELECT TABLE_NAME FROM USER_TABLES", db);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (name.StartsWith(prefix + "_", StringComparison.Ordinal))
                result.Add(name);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static ForeignKey[] ReadForeignKeys(OracleConnection db, string prefix)
    {
        // USER_CONSTRAINTS.R_CONSTRAINT_NAME resolves to the referenced PK/UK.
        // USER_CONS_COLUMNS.POSITION proves both ordered sides of each FK.
        const string sql = """
            SELECT c.TABLE_NAME, c.CONSTRAINT_NAME, child.COLUMN_NAME,
                   parent.TABLE_NAME, target.COLUMN_NAME, child.POSITION
            FROM USER_CONSTRAINTS c
            JOIN USER_CONS_COLUMNS child ON
                 child.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            JOIN USER_CONSTRAINTS parent ON
                 parent.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME
            JOIN USER_CONS_COLUMNS target ON
                 target.CONSTRAINT_NAME = parent.CONSTRAINT_NAME
                 AND target.POSITION = child.POSITION
            WHERE c.CONSTRAINT_TYPE = 'R'
            ORDER BY c.TABLE_NAME, c.CONSTRAINT_NAME, child.POSITION
            """;
        var rows = new List<(string Table, string Name, string Local,
                             string Target, string Remote, int Position)>();
        using var command = new OracleCommand(sql, db);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (table.StartsWith(prefix + "_", StringComparison.Ordinal))
                rows.Add((table, reader.GetString(1), reader.GetString(2),
                          reader.GetString(3), reader.GetString(4),
                          Convert.ToInt32(reader.GetValue(5))));
        }
        return rows.GroupBy(row => (row.Table, row.Name, row.Target))
            .Select(group => new ForeignKey(
                group.Key.Table, group.Key.Name, group.Key.Target,
                group.OrderBy(row => row.Position).Select(row => row.Local).ToArray(),
                group.OrderBy(row => row.Position).Select(row => row.Remote).ToArray()))
            .OrderBy(fk => fk.Table, StringComparer.Ordinal)
            .ThenBy(fk => fk.Name, StringComparer.Ordinal).ToArray();
    }
}
