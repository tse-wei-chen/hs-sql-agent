using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// InnoDB ALTER FK dependency graph oracle. Real metadata after EACH DDL
/// determines index survival, not a guessed Catalog physical DROP.
/// </summary>
public sealed class SqlCoreMySqlAlterFkGraphLiveParityTests
{
    private sealed record Scenario(string Name, string[] Actions,
        string[] RemainingFk, string[] ExpectedExplicit);
    private sealed record Physical(string Name, string Column, int Position);
    private const string Shared =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
        "CONSTRAINT uq_pair UNIQUE(id,code));" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, note INT, " +
        "CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id)," +
        "CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code));";
    private static readonly Scenario[] Cases =
    [
        new("drop_short_then_add_noncovering",
            ["ALTER TABLE child DROP FOREIGN KEY fk_short",
             "CREATE INDEX ix_p ON child(p)"],
            ["fk_long"], ["ix_p"]),
        new("drop_long_then_replace_with_backup",
            ["ALTER TABLE child DROP FOREIGN KEY fk_long",
             "CREATE INDEX ix_primary_support ON child(p,q)",
             "CREATE INDEX ix_backup ON child(p)",
             "DROP INDEX ix_primary_support ON child"],
            ["fk_short"], ["ix_backup"]),
        new("drop_two_atomic_then_add_disjoint",
            ["ALTER TABLE child DROP FOREIGN KEY fk_short, DROP FOREIGN KEY fk_long",
             "CREATE INDEX ix_note ON child(note)"],
            [], ["ix_note"])
    ];
    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    public static IEnumerable<object[]> CaseIds => Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task AlterDropRecomputesExactForeignKeysAndKnownExplicitIndexDelta(string name)
    {
        var scenario = Cases.Single(x => x.Name == name);
        var model = SqlCoreFacade.AnalyzeScript(Shared +
            string.Join(";", scenario.Actions) + ";", SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await WithDatabase(async (db, database) =>
        {
            await Execute(db, model.Statements[0].Syntax.Text);
            await Execute(db, model.Statements[1].Syntax.Text);
            var initial = await ReadPhysical(db, database);
            Assert.True(Supports(initial, ["p"]));
            Assert.True(Supports(initial, ["p","q"]));
            Assert.Equal(new[] { "fk_long", "fk_short" },
                await ReadForeignKeys(db, database));
            for (var i = 0; i < scenario.Actions.Length; i++)
            {
                await Execute(db, model.Statements[i + 2].Syntax.Text);
                var after = await ReadPhysical(db, database);
                Assert.Equal(scenario.RemainingFk, await ReadForeignKeys(db, database));
                foreach (var currentFk in scenario.RemainingFk)
                {
                    var prefix = currentFk == "fk_short" ? new[] { "p" } : ["p","q"];
                    Assert.True(Supports(after, prefix));
                }
                Assert.Contains(after, row => row.Name == "PRIMARY" && row.Column == "id");
                Assert.Equal(after.Length, after
                    .Select(row => (row.Name, row.Position)).Distinct().Count());
            }
            var table = model.FinalCatalog.Tables.Single(t => t.Name == "child");
            Assert.Equal(scenario.RemainingFk, table.Constraints
                .Where(c => c.Kind == SqlCatalogConstraintKind.ForeignKey)
                .Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(scenario.ExpectedExplicit,
                table.Indexes.Select(idx => idx.Name).ToArray());
            var physicalFinal = await ReadPhysical(db, database);
            foreach (var index in scenario.ExpectedExplicit)
                Assert.Contains(physicalFinal, x => x.Name == index);
        });
    }

    [Fact]
    public async Task DatabaseAcceptsAddingFkOnEmptyChildButStaticCatalogCannotProveRows()
    {
        const string definition =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT);";
        const string add =
            "ALTER TABLE child ADD CONSTRAINT fk_new FOREIGN KEY(pid) REFERENCES parent(id);";
        var model = SqlCoreFacade.AnalyzeScript(definition + add,
            SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty(), Linux,
            TestContext.Current.CancellationToken);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, model.Statements[2].Status);
        Assert.Contains(model.Statements[2].Diagnostics,
            d => d.Code == "SQL_SCHEMA_DEPENDENCY_UNPROVEN");
        Assert.DoesNotContain(model.Statements[2].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
        await WithDatabase(async (db, database) =>
        {
            await Execute(db, model.Statements[0].Syntax.Text);
            await Execute(db, model.Statements[1].Syntax.Text);
            await Execute(db, add);
            Assert.Equal(new[] { "fk_new" }, await ReadForeignKeys(db, database));
            Assert.True(Supports(await ReadPhysical(db, database), ["pid"]));
        });
    }

    private static bool Supports(Physical[] physical, string[] prefix) =>
        physical.GroupBy(x => x.Name).Any(group =>
            prefix.SequenceEqual(group.OrderBy(x => x.Position)
                .Take(prefix.Length).Select(x => x.Column)));

    private static async Task<Physical[]> ReadPhysical(MySqlConnection db, string name)
    {
        const string sql = """
            SELECT INDEX_NAME,COLUMN_NAME,SEQ_IN_INDEX
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA=@schema AND TABLE_NAME='child'
             ORDER BY INDEX_NAME,SEQ_IN_INDEX
            """;
        var rows = new List<Physical>();
        await using var cmd = new MySqlCommand(sql,db);
        cmd.Parameters.AddWithValue("@schema", name);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(new Physical(reader.GetString(0),reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2))));
        return rows.ToArray();
    }

    private static async Task<string[]> ReadForeignKeys(MySqlConnection db, string name)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS
             WHERE CONSTRAINT_SCHEMA=@schema AND TABLE_NAME='child'
             ORDER BY CONSTRAINT_NAME
            """;
        var symbols = new List<string>();
        await using var cmd = new MySqlCommand(sql,db);
        cmd.Parameters.AddWithValue("@schema",name);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            symbols.Add(reader.GetString(0));
        return symbols.ToArray();
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"),"true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION must be set in CI");
        Assert.Skip("Real MySQL requires TEST_MYSQL_CONNECTION");
        throw new InvalidOperationException("Unreachable");
    }

    private static async Task WithDatabase(Func<MySqlConnection,string,Task> run)
    {
        var root = ConnectionString();
        var schema = "sqlcore_alterfk_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var mode = new MySqlCommand("SELECT @@lower_case_table_names",admin))
            Assert.Equal(0,Convert.ToInt32(await mode.ExecuteScalarAsync(
                TestContext.Current.CancellationToken)));
        await Execute(admin,"CREATE DATABASE `" + schema + "`");
        try
        {
            var cs = new MySqlConnectionStringBuilder(root)
            {
                Database = schema,Pooling = false
            };
            await using var db = new MySqlConnection(cs.ConnectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await run(db,schema);
        }
        finally
        {
            await Execute(admin,"DROP DATABASE IF EXISTS `" + schema + "`");
        }
    }

    private static async Task Execute(MySqlConnection db, string sql)
    {
        await using var cmd = new MySqlCommand(sql,db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
