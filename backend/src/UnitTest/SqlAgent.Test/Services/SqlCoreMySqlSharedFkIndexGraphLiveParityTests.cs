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
/// Real MySQL 8 InnoDB parity for shared FK-prefix dependency groups.
/// InnoDB may silently replace FK auto indexes. Verify FKs and index prefix
/// coverage, never assume any implicit FK-named physical index was removed.
/// </summary>
public sealed class SqlCoreMySqlSharedFkIndexGraphLiveParityTests
{
    private sealed record Scenario(string Name, string[] Commands, string[] ExplicitIndexes,
        bool HasUnrelatedThirdFk);
    private sealed record Physical(string Name, string Column, int Ordinal, int NonUnique);
    private const string Shared =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
        "CONSTRAINT uq_pair UNIQUE(id,code));" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, note INT, " +
        "CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id), " +
        "CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code));";
    private const string Mixed =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
        "CONSTRAINT uq_pair UNIQUE(id,code));" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, r INT, note INT, " +
        "CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id), " +
        "CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code), " +
        "CONSTRAINT fk_independent FOREIGN KEY(r) REFERENCES parent(id));";
    private static readonly Scenario[] Scenarios =
    [
        new("shared_unrelated",
            ["CREATE INDEX ix_note ON child(note)"],
            ["ix_note"], false),
        new("shared_cover_backup_drop",
            ["CREATE INDEX ix_shared ON child(p,q,note)",
             "CREATE INDEX ix_backup ON child(p,q)",
             "DROP INDEX ix_shared ON child"],
            ["ix_backup"], false),
        new("shared_two_alternatives",
            ["ALTER TABLE child ADD KEY ix_shared(p,q)",
             "ALTER TABLE child ADD INDEX ix_backup(p,q,note)",
             "ALTER TABLE child DROP KEY ix_shared"],
            ["ix_backup"], false),
        new("shared_and_disjoint",
            ["CREATE INDEX ix_shared ON child(p,q)",
             "CREATE INDEX ix_third ON child(r)",
             "CREATE INDEX ix_note ON child(note)"],
            ["ix_shared", "ix_third", "ix_note"], true)
    ];
    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });
    public static IEnumerable<object[]> Cases => Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void CorpusHasFourDistinctPhysicalDependencyTransitions() =>
        Assert.Equal(4, Scenarios.Select(x => string.Join(";", x.Commands)).Distinct().Count());

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FullSharedSupportGraphMatchesLiveFkAndPhysicalIndexDictionary(string name)
    {
        var scenario = Scenarios.Single(x => x.Name == name);
        var script = (scenario.HasUnrelatedThirdFk ? Mixed : Shared) +
            string.Join(";", scenario.Commands) + ";";
        var model = SqlCoreFacade.AnalyzeScript(script, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        Assert.All(model.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        await WithDatabase(async (db, database) =>
        {
            await Execute(db, model.Statements[0].Syntax.Text);
            await Execute(db, model.Statements[1].Syntax.Text);
            var initial = await ReadPhysical(db, database);
            var required = scenario.HasUnrelatedThirdFk
                ? new[] { "fk_independent", "fk_long", "fk_short" }
                : new[] { "fk_long", "fk_short" };
            Assert.Equal(required, await ReadFkSymbols(db, database));
            Assert.Contains(initial, row => row.Name == "PRIMARY" && row.Column == "id");
            Assert.True(Supports(initial, ["p"]));
            Assert.True(Supports(initial, ["p", "q"]));
            if (scenario.HasUnrelatedThirdFk)
                Assert.True(Supports(initial, ["r"]));
            Assert.Empty(model.Statements[1].CatalogAfter.Tables
                .Single(t => t.Name == "child").Indexes);

            var previous = initial;
            for (var i = 0; i < scenario.Commands.Length; i++)
            {
                await Execute(db, model.Statements[i + 2].Syntax.Text);
                var physical = await ReadPhysical(db, database);
                Assert.Equal(required, await ReadFkSymbols(db, database));
                Assert.True(Supports(physical, ["p"]));
                Assert.True(Supports(physical, ["p", "q"]));
                if (scenario.HasUnrelatedThirdFk)
                    Assert.True(Supports(physical, ["r"]));
                Assert.Equal(physical.Length,
                    physical.Select(x => x.Name + ":" + x.Ordinal).Distinct().Count());
                if (scenario.Commands[i].StartsWith("DROP INDEX", StringComparison.Ordinal)
                    || scenario.Commands[i].Contains("DROP KEY", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(physical, x => x.Name == "ix_shared");
                    Assert.Contains(physical, x => x.Name == "ix_backup" && x.Column == "p");
                }
                previous = physical;
            }
            Assert.NotEmpty(previous);
            var known = model.FinalCatalog.Tables.Single(t => t.Name == "child");
            Assert.Equal(scenario.ExplicitIndexes, known.Indexes.Select(x => x.Name).ToArray());
            Assert.Equal(required, known.Constraints
                .Where(x => x.Kind == SqlCatalogConstraintKind.ForeignKey)
                .Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        });
    }

    [Fact]
    public async Task WithoutFullReplacementSharedGroupDoesNotProveDropOfLongFkSupporter()
    {
        var sql = Shared +
            "CREATE INDEX ix_shared ON child(p,q);" +
            "CREATE INDEX ix_partial ON child(p);" +
            "DROP INDEX ix_shared ON child;";
        var model = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux, TestContext.Current.CancellationToken);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, model.Statements[2].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, model.Statements[3].Status);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, model.Statements[4].Status);
        await WithDatabase(async (db, database) =>
        {
            foreach (var statement in model.Statements.Take(4))
                await Execute(db, statement.Syntax.Text);
            var before = await ReadPhysical(db, database);
            // InnoDB may have retained an implicit long-FK support index.
            // Require real DROP rejection only when it was the last one.
            var otherSupportsLong = before
                .Where(x => x.Name != "ix_shared")
                .GroupBy(x => x.Name)
                .Any(group => new[] { "p", "q" }.SequenceEqual(
                    group.OrderBy(x => x.Ordinal).Take(2).Select(x => x.Column)));
            if (!otherSupportsLong)
            {
                await Assert.ThrowsAsync<MySqlException>(async () =>
                    await Execute(db, "DROP INDEX ix_shared ON child"));
                Assert.Equal(before, await ReadPhysical(db, database));
            }
        });
    }

    private static bool Supports(Physical[] physical, string[] prefix) =>
        physical.GroupBy(x => x.Name).Any(group =>
            prefix.SequenceEqual(group.OrderBy(x => x.Ordinal)
                .Take(prefix.Length).Select(x => x.Column)));

    private static async Task<Physical[]> ReadPhysical(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX, NON_UNIQUE
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA=@db AND TABLE_NAME='child'
             ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        var rows = new List<Physical>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(new Physical(reader.GetString(0), reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3))));
        return rows.ToArray();
    }

    private static async Task<string[]> ReadFkSymbols(MySqlConnection db, string database)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS
             WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='child'
             ORDER BY CONSTRAINT_NAME
            """;
        var symbols = new List<string>();
        await using var cmd = new MySqlCommand(sql, db);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            symbols.Add(reader.GetString(0));
        return symbols.ToArray();
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEST_MYSQL_CONNECTION is required for live FK graph parity.");
        Assert.Skip("Requires real MySQL with TEST_MYSQL_CONNECTION.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static async Task WithDatabase(Func<MySqlConnection, string, Task> body)
    {
        var root = ConnectionString();
        var database = "sqlcore_fkgraph_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(root);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var mode = new MySqlCommand("SELECT @@lower_case_table_names", admin))
            Assert.Equal(0, Convert.ToInt32(await mode.ExecuteScalarAsync(
                TestContext.Current.CancellationToken)));
        await Execute(admin, "CREATE DATABASE `" + database + "`");
        try
        {
            var cs = new MySqlConnectionStringBuilder(root)
            {
                Database = database, Pooling = false
            };
            await using var db = new MySqlConnection(cs.ConnectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await body(db, database);
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + database + "`");
        }
    }

    private static async Task Execute(MySqlConnection db, string sql)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
