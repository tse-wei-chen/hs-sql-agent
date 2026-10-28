using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Prove two-source PostgreSQL OUTER USING: LEFT uses left key, RIGHT uses
/// right key, FULL uses COALESCE. The merged key must never inherit the
/// null-extension of an individual input without this join-kind proof.
/// </summary>
public sealed class SqlCorePostgresViewOuterUsingProofTests
{
    private const string Tables =
        "CREATE TABLE app.a(id INTEGER NOT NULL, tenant INTEGER NOT NULL, a_note TEXT);" +
        "CREATE TABLE app.b(id INTEGER, tenant INTEGER NOT NULL, b_note TEXT);";
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, catalog ?? SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("LEFT JOIN", false, false, true)]
    [InlineData("LEFT OUTER JOIN", false, false, true)]
    [InlineData("RIGHT JOIN", true, true, true)]
    [InlineData("RIGHT OUTER JOIN", true, true, true)]
    [InlineData("FULL JOIN", true, true, true)]
    [InlineData("FULL OUTER JOIN", true, true, true)]
    public void MergedNamedKeyAndQualifiedPhysicalKeysKeepDistinctNullability(
        string join, bool mergedNullable, bool leftNullable, bool rightNullable)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id AS shared_id, a.id AS left_id, " +
            "b.id AS right_id FROM app.a a " + join + " app.b b USING (id);");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "shared_id", "left_id", "right_id" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { mergedNullable, leftNullable, rightNullable },
            view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "app.a", "app.b" },
            view.Dependencies.Select(d => d!.DisplayName));
        Assert.Contains("id", view.SourceBindings[0].Columns);
        Assert.Contains("id", view.SourceBindings[1].Columns);
    }

    [Theory]
    [InlineData("LEFT JOIN", false)]
    [InlineData("RIGHT JOIN", true)]
    [InlineData("FULL JOIN", true)]
    public void MergedStarRespectsKeyOrderAndPhysicalColumnOwnership(string join, bool keyNullable)
    {
        var sql =
            "CREATE TABLE app.left_data(id INTEGER NOT NULL, tenant INTEGER NOT NULL, left_note TEXT);" +
            "CREATE TABLE app.right_data(id INTEGER, tenant INTEGER NOT NULL, right_note TEXT);" +
            "CREATE VIEW app.v AS SELECT * FROM app.left_data l " + join +
            " app.right_data r USING (tenant,id);";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "tenant", "id", "left_note", "right_note" },
            view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { false, keyNullable, true, true },
            view.Columns.Select(c => c.Nullable));
        Assert.Equal(new[] { "tenant", "id", "left_note" }, view.SourceBindings[0].Columns);
        Assert.Equal(new[] { "right_note", "tenant", "id" }, view.SourceBindings[1].Columns);
    }

    [Fact]
    public void RightUsingMergedKeyUsesRightNonNullableSourceEvenWhenLeftIsNullable()
    {
        var result = Analyze(
            "CREATE TABLE app.a(id INTEGER, a_note TEXT);" +
            "CREATE TABLE app.b(id INTEGER NOT NULL, b_note TEXT);" +
            "CREATE VIEW app.v AS SELECT id, a.id AS a_id, b.id AS b_id " +
            "FROM app.a a RIGHT JOIN app.b b USING (id);");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { false, true, false }, view.Columns.Select(c => c.Nullable));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void FullUsingMergedCoalesceIsNullableWhenEitherBaseKeyIsNullable(
        bool leftNullable, bool rightNullable, bool mergedNullable)
    {
        var left = leftNullable ? "INTEGER" : "INTEGER NOT NULL";
        var right = rightNullable ? "INTEGER" : "INTEGER NOT NULL";
        var result = Analyze(
            $"CREATE TABLE app.a(id {left}, a_note TEXT);" +
            $"CREATE TABLE app.b(id {right}, b_note TEXT);" +
            "CREATE VIEW app.v AS SELECT id FROM app.a a FULL JOIN app.b b USING (id);");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(mergedNullable, Assert.Single(Assert.Single(result.FinalCatalog.Views).Columns).Nullable);
    }

    [Theory]
    [InlineData("LEFT JOIN")]
    [InlineData("RIGHT OUTER JOIN")]
    [InlineData("FULL OUTER JOIN")]
    public void OuterUsingKeysOnBothSourcesAreDropProtectedButUnrelatedColumnsAreFree(string join)
    {
        var setup = Tables +
            "CREATE VIEW app.v AS SELECT a.a_note FROM app.a a " + join + " app.b b USING (id);";
        foreach (var table in new[] { "a", "b" })
        {
            var dropped = Analyze(setup + $"ALTER TABLE app.{table} DROP COLUMN id;");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, dropped.Statements[3].Status);
            Assert.Empty(dropped.Statements[3].Changes);
        }
        var allowed = Analyze(setup + "ALTER TABLE app.b DROP COLUMN b_note;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[3].Status);
    }

    [Theory]
    [InlineData("LEFT JOIN")]
    [InlineData("RIGHT JOIN")]
    [InlineData("FULL JOIN")]
    public void OuterUsingCanUseMergedKeyInWhereWithTypedBinding(string join)
    {
        var result = Analyze(Tables +
            "CREATE VIEW app.v AS SELECT id AS exported FROM app.a a " + join +
            " app.b b USING (id) WHERE id > 0;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal("exported", Assert.Single(view.Columns).Name);
    }

    [Fact]
    public void FullUsingReplacementMaintainsColumnContractButBlocksDependentNullabilityChange()
    {
        var sql =
            "CREATE TABLE app.a(id INTEGER NOT NULL, a_note TEXT);" +
            "CREATE TABLE app.b(id INTEGER, b_note TEXT);" +
            "CREATE VIEW app.v AS SELECT id FROM app.a a LEFT JOIN app.b b USING (id);" +
            "CREATE VIEW app.child AS SELECT id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT id FROM app.a a FULL JOIN app.b b USING (id);";
        var result = Analyze(sql);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[4].Status);
        Assert.Empty(result.Statements[4].Changes);
        Assert.False(Assert.Single(result.FinalCatalog.Views.Where(v => v.Name == "app.v"))
            .Columns[0].Nullable);
    }

    [Theory]
    [InlineData("SELECT * FROM app.a a FULL JOIN app.b b USING (missing)")]
    [InlineData("SELECT * FROM app.a a LEFT JOIN app.b b USING (id,id)")]
    [InlineData("SELECT * FROM app.a a RIGHT JOIN app.b b USING (id) FULL JOIN app.c c USING (id)")]
    [InlineData("SELECT * FROM app.a a FULL JOIN app.a x USING (id)")]
    [InlineData("SELECT * FROM app.a a FULL JOIN app.b b USING ()")]
    public void UnprovenOuterUsingRemainsFailClosed(string select)
    {
        var result = Analyze(Tables + "CREATE TABLE app.c(id INTEGER);" +
            "CREATE VIEW app.v AS " + select + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsCannotInheritOuterUsingProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT id FROM app.a a FULL JOIN app.b b USING (id);",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public async Task LivePostgresLeftRightFullUsingCoalesceRowsAndColumnOrdering()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required for PostgreSQL parity.");
            Assert.Skip("Live PostgreSQL parity requires TEST_POSTGRES_CONNECTION.");
            return;
        }

        var schema = "sqlcore_outer_using_" + Guid.NewGuid().ToString("N");
        var setup =
            $"CREATE TABLE {schema}.a(id INTEGER NOT NULL, a_note TEXT);" +
            $"CREATE TABLE {schema}.b(id INTEGER, b_note TEXT);" +
            $"CREATE VIEW {schema}.v_left AS SELECT * FROM {schema}.a a LEFT JOIN {schema}.b b USING (id);" +
            $"CREATE VIEW {schema}.v_right AS SELECT * FROM {schema}.a a RIGHT JOIN {schema}.b b USING (id);" +
            $"CREATE VIEW {schema}.v_full AS SELECT * FROM {schema}.a a FULL JOIN {schema}.b b USING (id);";
        var analysis = Analyze(setup);
        Assert.Empty(analysis.Diagnostics);
        Assert.All(analysis.Statements,
            s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Execute("CREATE SCHEMA " + schema);
        foreach (var stmt in analysis.Statements)
            await Execute(stmt.Syntax.Text);
        await Execute($"INSERT INTO {schema}.a VALUES (1,'one'),(2,'two');");
        await Execute($"INSERT INTO {schema}.b VALUES (1,'one-b'),(3,'three-b'),(NULL,'null-b');");
        foreach (var name in new[] { "v_left", "v_right", "v_full" })
        {
            var expected = name switch
            {
                "v_left" => new[] { "1|one|one-b", "2|two|<null>" },
                "v_right" => new[] { "1|one|one-b", "3|<null>|three-b", "<null>|<null>|null-b" },
                _ => new[] { "1|one|one-b", "2|two|<null>", "3|<null>|three-b", "<null>|<null>|null-b" }
            };
            var rows = new List<string>();
            await using (var cmd = new NpgsqlCommand(
                $"SELECT * FROM {schema}.{name} ORDER BY id NULLS LAST", db, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            {
                Assert.Equal(new[] { "id", "a_note", "b_note" },
                    Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                    rows.Add(
                        (reader.IsDBNull(0) ? "<null>" : reader.GetInt32(0).ToString()) + "|" +
                        (reader.IsDBNull(1) ? "<null>" : reader.GetString(1)) + "|" +
                        (reader.IsDBNull(2) ? "<null>" : reader.GetString(2)));
            }
            Assert.Equal(expected, rows);
        }
        foreach (var table in new[] { "a", "b" })
        {
            await Execute("SAVEPOINT outer_using_guard");
            await Assert.ThrowsAsync<PostgresException>(() =>
                Execute($"ALTER TABLE {schema}.{table} DROP COLUMN id;"));
            await Execute("ROLLBACK TO SAVEPOINT outer_using_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
