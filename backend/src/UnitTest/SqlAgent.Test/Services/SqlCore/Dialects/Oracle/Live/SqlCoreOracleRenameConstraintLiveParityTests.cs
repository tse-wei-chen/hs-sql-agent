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
/// Real Oracle Free USER_CONSTRAINTS + USER_CONS_COLUMNS parity after each
/// ALTER TABLE RENAME CONSTRAINT. DDL implicitly commits, so every scenario
/// owns unique physical names and explicitly purges tables on any failure.
/// </summary>
public sealed class SqlCoreOracleRenameConstraintLiveParityTests
{
    private sealed record Key(string Table, string Name, string Kind, string Target,
        string[] Local, string[] Remote);
    private sealed record Scenario(string Name, string Sql);
    private static readonly Scenario[] Scenarios =
    [
        new("parent_pk_and_child_fk",
            "CREATE TABLE {T1}(ID NUMBER NOT NULL, CONSTRAINT {C1} PRIMARY KEY(ID));" +
            "CREATE TABLE {T2}(PID NUMBER, CONSTRAINT {C2} FOREIGN KEY(PID) REFERENCES {T1}(ID));" +
            "ALTER TABLE {T1} RENAME CONSTRAINT {C1} TO {C3};" +
            "ALTER TABLE {T2} RENAME CONSTRAINT {C2} TO {C4};"),
        new("unique_rename_chain_and_drop",
            "CREATE TABLE {T1}(ID NUMBER, CODE NUMBER, CONSTRAINT {C1} UNIQUE(CODE));" +
            "ALTER TABLE {T1} RENAME CONSTRAINT {C1} TO {C2};" +
            "ALTER TABLE {T1} RENAME CONSTRAINT {C2} TO {C3};" +
            "ALTER TABLE {T1} DROP CONSTRAINT {C3};"),
        new("self_fk_rename_parent_key",
            "CREATE TABLE {T1}(ID NUMBER NOT NULL, PARENT_ID NUMBER, " +
            "CONSTRAINT {C1} PRIMARY KEY(ID), " +
            "CONSTRAINT {C2} FOREIGN KEY(PARENT_ID) REFERENCES {T1}(ID));" +
            "ALTER TABLE {T1} RENAME CONSTRAINT {C1} TO {C3};" +
            "ALTER TABLE {T1} RENAME CONSTRAINT {C2} TO {C4};"),
        new("composite_remote_key_rename",
            "CREATE TABLE {T1}(A NUMBER NOT NULL, B NUMBER NOT NULL, " +
            "CONSTRAINT {C1} UNIQUE(A,B));" +
            "CREATE TABLE {T2}(X NUMBER, Y NUMBER, CONSTRAINT {C2} FOREIGN KEY(X,Y) REFERENCES {T1}(A,B));" +
            "ALTER TABLE {T1} RENAME CONSTRAINT {C1} TO {C3};" +
            "ALTER TABLE {T2} RENAME CONSTRAINT {C2} TO {C4};")
    ];
    public static IEnumerable<object[]> Cases => Scenarios.Select(x => new object[] { x.Name });

    [Fact]
    public void FourDistinctRealOracleConstraintLifecycleScripts() =>
        Assert.Equal(4, Scenarios.Select(s => s.Sql).Distinct().Count());

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryTransitionMatchesRealOracleConstraintDictionary(string id)
    {
        var scenario = Scenarios.Single(s => s.Name == id);
        var prefix = "HSARC_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var sql = scenario.Sql;
        foreach (var suffix in new[] { "T1", "T2", "C1", "C2", "C3", "C4" })
            sql = sql.Replace("{" + suffix + "}", prefix + "_" + suffix,
                StringComparison.Ordinal);

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
                var actual = ReadKeys(db, prefix);
                var expected = step.CatalogAfter.Tables.SelectMany(t => t.Constraints.Select(c =>
                    new Key(t.QualifiedName!.Parts[^1], c.Name, c.Kind switch
                    {
                        SqlCatalogConstraintKind.PrimaryKey => "P",
                        SqlCatalogConstraintKind.Unique => "U",
                        SqlCatalogConstraintKind.ForeignKey => "R",
                        _ => throw new InvalidOperationException("Unexpected key type")
                    }, c.Kind == SqlCatalogConstraintKind.ForeignKey
                        ? c.ReferencedIdentity!.Parts[^1] : "",
                       c.Columns.ToArray(), c.ReferencedColumns.ToArray())))
                    .OrderBy(x => x.Table, StringComparer.Ordinal)
                    .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
                Assert.Equal(expected.Length, actual.Length);
                for (var k = 0; k < expected.Length; k++)
                {
                    Assert.Equal(expected[k].Table, actual[k].Table);
                    Assert.Equal(expected[k].Name, actual[k].Name);
                    Assert.Equal(expected[k].Kind, actual[k].Kind);
                    Assert.Equal(expected[k].Target, actual[k].Target);
                    Assert.Equal(expected[k].Local, actual[k].Local);
                    Assert.Equal(expected[k].Remote, actual[k].Remote);
                }
            }
        }
        finally
        {
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
            throw new InvalidOperationException("TEST_ORACLE_CONNECTION required in CI.");
        Assert.Skip("Requires real Oracle and TEST_ORACLE_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task OpenWhenReady(OracleConnection db)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; ; i++)
        {
            try { await db.OpenAsync(ct); return; }
            catch (OracleException) when (i < 60 && !ct.IsCancellationRequested)
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
        var names = new List<string>();
        using var command = new OracleCommand("SELECT TABLE_NAME FROM USER_TABLES", db);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (name.StartsWith(prefix + "_", StringComparison.Ordinal))
                names.Add(name);
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private static Key[] ReadKeys(OracleConnection db, string prefix)
    {
        // Dictionary key is constraint name. FK's referenced key is resolved
        // through R_CONSTRAINT_NAME, not inferred from target column names.
        const string sql = """
            SELECT c.TABLE_NAME, c.CONSTRAINT_NAME, c.CONSTRAINT_TYPE,
                   CASE WHEN c.CONSTRAINT_TYPE = 'R' THEN parent.TABLE_NAME ELSE '' END,
                   local.COLUMN_NAME, local.POSITION,
                   CASE WHEN c.CONSTRAINT_TYPE = 'R' THEN remote.COLUMN_NAME ELSE '' END
            FROM USER_CONSTRAINTS c
            JOIN USER_CONS_COLUMNS local ON local.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            LEFT JOIN USER_CONSTRAINTS parent ON parent.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME
            LEFT JOIN USER_CONS_COLUMNS remote ON
                 remote.CONSTRAINT_NAME = parent.CONSTRAINT_NAME
                 AND remote.POSITION = local.POSITION
            WHERE c.CONSTRAINT_TYPE IN ('P', 'U', 'R')
            ORDER BY c.TABLE_NAME, c.CONSTRAINT_NAME, local.POSITION
            """;
        var rows = new List<(string Table, string Name, string Kind, string Target,
                             string Local, string Remote, int Position)>();
        using var cmd = new OracleCommand(sql, db);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (!table.StartsWith(prefix + "_", StringComparison.Ordinal))
                continue;
            // Oracle treats SQL empty strings as NULL. Non-FK keys have
            // no target relation or remote key columns by definition.
            rows.Add((table, reader.GetString(1), reader.GetString(2),
                      reader.IsDBNull(3) ? "" : reader.GetString(3),
                      reader.GetString(4),
                      reader.IsDBNull(6) ? "" : reader.GetString(6),
                      Convert.ToInt32(reader.GetValue(5))));
        }
        return rows.GroupBy(x => (x.Table, x.Name, x.Kind, x.Target))
            .Select(g => new Key(g.Key.Table, g.Key.Name, g.Key.Kind, g.Key.Target,
                g.OrderBy(x => x.Position).Select(x => x.Local).ToArray(),
                g.Key.Kind == "R"
                    ? g.OrderBy(x => x.Position).Select(x => x.Remote).ToArray()
                    : Array.Empty<string>()))
            .OrderBy(x => x.Table, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}
