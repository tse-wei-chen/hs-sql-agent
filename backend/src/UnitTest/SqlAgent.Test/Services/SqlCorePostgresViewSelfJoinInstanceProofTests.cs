using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>PostgreSQL View self-joins bind FROM instances by alias and
/// preserve Catalog dependencies at their shared physical table owner.</summary>
public sealed class SqlCorePostgresViewSelfJoinInstanceProofTests
{
    private const string Table = "CREATE TABLE app.employees(id INTEGER NOT NULL, manager_id INTEGER, team INTEGER, name TEXT NOT NULL);";
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("JOIN", false, false)]
    [InlineData("LEFT JOIN", false, true)]
    [InlineData("RIGHT JOIN", true, false)]
    [InlineData("FULL JOIN", true, true)]
    public void SelfJoinQualifiedColumnsHavePerInstanceNullExtension(string kind, bool leftNullable, bool rightNullable)
    {
        var result = Analyze(Table +
            "CREATE VIEW app.v AS SELECT e.id AS employee_id, m.id AS manager_id " +
            "FROM app.employees e " + kind + " app.employees m ON e.manager_id = m.id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "employee_id", "manager_id" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { leftNullable, rightNullable }, view.Columns.Select(c => c.Nullable));
        Assert.Single(view.Dependencies);
        Assert.Equal("app.employees", view.Dependencies[0].DisplayName);
        Assert.Contains("manager_id", Assert.Single(view.SourceBindings).Columns);
        Assert.Contains("id", Assert.Single(view.SourceBindings).Columns);
    }

    [Fact]
    public void SelfJoinQualifiedWildcardResolvesOnlyChosenInstance()
    {
        var result = Analyze(Table +
            "CREATE VIEW app.v AS SELECT m.* FROM app.employees e " +
            "LEFT JOIN app.employees m ON e.manager_id = m.id;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "id", "manager_id", "team", "name" },
            view.Columns.Select(c => c.Name));
        Assert.All(view.Columns, c => Assert.True(c.Nullable));
        Assert.Single(view.SourceBindings);
    }

    [Theory]
    [InlineData("LEFT JOIN", false)]
    [InlineData("RIGHT JOIN", false)]
    [InlineData("FULL JOIN", false)]
    public void SelfUsingMergedIdIsUniqueAndPhysicalKeysStayProtected(string kind, bool nullable)
    {
        var result = Analyze(Table +
            "CREATE VIEW app.v AS SELECT * FROM app.employees e " + kind +
            " app.employees m USING (id);");
        // The remaining manager_id/team/name names are duplicates; View
        // output must reject collisions rather than silently rename.
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.Empty(result.FinalCatalog.Views);

        var named = Analyze(Table +
            "CREATE VIEW app.v AS SELECT id AS joined_id FROM app.employees e " +
            kind + " app.employees m USING (id);");
        Assert.Empty(named.Diagnostics);
        var view = Assert.Single(named.FinalCatalog.Views);
        Assert.Equal(nullable, Assert.Single(view.Columns).Nullable);
        Assert.Single(view.SourceBindings);
        Assert.Contains("id", view.SourceBindings[0].Columns);
    }

    [Fact]
    public void TwoAliasesFromSameTableRemainDistinctThroughGroupedOrAndNullSafeOn()
    {
        var result = Analyze(Table +
            "CREATE VIEW app.v AS SELECT e.id AS child, m.id AS parent " +
            "FROM app.employees e JOIN app.employees m " +
            "ON (e.manager_id = m.id OR e.team IS NOT DISTINCT FROM m.team) AND e.id <> m.id;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Contains("team", Assert.Single(view.SourceBindings).Columns);
        Assert.Contains("id", view.SourceBindings[0].Columns);
    }

    [Fact]
    public void ThreeSourceRepeatedTableAliasGetsCorrectRightNullabilityAndDependencies()
    {
        var result = Analyze(Table +
            "CREATE TABLE app.teams(id INTEGER NOT NULL, label TEXT);" +
            "CREATE VIEW app.v AS SELECT e.id AS employee_id, m.id AS manager_id, t.label " +
            "FROM app.employees e LEFT JOIN app.teams t ON e.team = t.id " +
            "LEFT JOIN app.employees m ON e.manager_id = m.id;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { false, true, true }, view.Columns.Select(c => c.Nullable));
        Assert.Equal(2, view.SourceBindings.Count);
        Assert.Equal(new[] { "app.employees", "app.teams" },
            view.Dependencies.Select(d => d.DisplayName));
    }

    [Fact]
    public void SelfJoinUsingThenOnCanBindUniqueStageMergedKey()
    {
        var result = Analyze(Table +
            "CREATE TABLE app.roles(employee_id INTEGER NOT NULL, role_name TEXT);" +
            "CREATE VIEW app.v AS SELECT id AS shared_id, r.role_name " +
            "FROM app.employees e JOIN app.employees m USING (id) " +
            "LEFT JOIN app.roles r ON id = r.employee_id;");
        Assert.Empty(result.Diagnostics);
        var view = Assert.Single(result.FinalCatalog.Views);
        Assert.Equal(new[] { "shared_id", "role_name" }, view.Columns.Select(c => c.Name));
        Assert.Equal(2, view.Dependencies.Count);
    }

    [Fact]
    public void DroppingAnyPhysicalSelfJoinColumnIsBlockedAndUnrelatedColumnIsFree()
    {
        var script = Table +
            "CREATE VIEW app.v AS SELECT e.id AS child, m.id AS parent " +
            "FROM app.employees e JOIN app.employees m ON e.manager_id = m.id;";
        foreach (var column in new[] { "id", "manager_id" })
        {
            var blocked = Analyze(script + $"ALTER TABLE app.employees DROP COLUMN {column};");
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, blocked.Statements[2].Status);
            Assert.Empty(blocked.Statements[2].Changes);
        }
        var allowed = Analyze(script + "ALTER TABLE app.employees DROP COLUMN team;");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, allowed.Statements[2].Status);
    }

    [Fact]
    public void SelfJoinReplacementRebindsOnePhysicalOwnerAndKeepsChildView()
    {
        var sql = Table +
            "CREATE VIEW app.v AS SELECT e.id AS person_id FROM app.employees e " +
            "JOIN app.employees m ON e.manager_id = m.id;" +
            "CREATE VIEW app.child AS SELECT person_id FROM app.v;" +
            "CREATE OR REPLACE VIEW app.v AS SELECT m.id AS person_id FROM app.employees e " +
            "JOIN app.employees m ON e.team = m.team;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlCatalogOperation.ReplaceView,
            Assert.Single(result.Statements[3].Changes).Operation);
        Assert.Single(Assert.Single(result.FinalCatalog.Views, v => v.Name == "app.v").SourceBindings);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, Analyze(sql +
            "ALTER TABLE app.employees DROP COLUMN manager_id;").Statements[4].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, Analyze(sql +
            "ALTER TABLE app.employees DROP COLUMN team;").Statements[4].Status);
    }

    [Theory]
    [InlineData("SELECT id FROM app.employees e JOIN app.employees m ON e.manager_id = m.id")]
    [InlineData("SELECT e.id FROM app.employees e JOIN app.employees e ON e.manager_id = e.id")]
    [InlineData("SELECT e.id FROM app.employees e JOIN app.employees m ON e.id = e.team")]
    [InlineData("SELECT e.id FROM app.employees e JOIN app.employees m ON m.id = m.manager_id")]
    [InlineData("SELECT e.id FROM app.employees e JOIN app.employees m ON e.id = x.id")]
    [InlineData("SELECT * FROM app.employees e JOIN app.employees m ON e.manager_id = m.id")]
    [InlineData("SELECT e.id FROM app.employees e JOIN app.employees m USING (missing)")]
    [InlineData("SELECT e.id FROM app.employees e JOIN app.employees m USING (id,id)")]
    [InlineData("SELECT e.id FROM app.employees e JOIN app.employees m ON e.id = m.id JOIN app.employees x ON x.id = m.id JOIN app.employees y ON y.id = e.id JOIN app.employees z ON z.id = e.id")]
    public void AmbiguousOrUnprovenSelfJoinFailsClosed(string query)
    {
        var result = Analyze(Table + "CREATE VIEW app.v AS " + query + ";");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectCannotInheritPostgresSelfJoinProof(SqlAgentToolType dialect)
    {
        var result = Analyze("CREATE VIEW app.v AS SELECT e.id FROM app.employees e " +
            "JOIN app.employees m ON e.manager_id = m.id;", dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.Empty(result.FinalCatalog.Views);
    }

    [Fact]
    public async Task LivePostgresSelfJoinRowsAndPhysicalDropGuard()
    {
        var conn = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(conn))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION");
            return;
        }
        var schema = "sqlcore_selfjoin_" + Guid.NewGuid().ToString("N");
        var script =
            $"CREATE TABLE {schema}.employees(id INTEGER NOT NULL, manager_id INTEGER, team INTEGER, name TEXT NOT NULL);" +
            $"CREATE VIEW {schema}.v AS SELECT e.name AS employee, m.name AS manager FROM {schema}.employees e " +
            $"LEFT JOIN {schema}.employees m ON e.manager_id = m.id;";
        var analysis = Analyze(script);
        Assert.Empty(analysis.Diagnostics);
        Assert.All(analysis.Statements, x => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        var view = Assert.Single(analysis.FinalCatalog.Views);
        Assert.Equal(new[] { false, true }, view.Columns.Select(c => c.Nullable));
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task Exec(string command)
        {
            await using var cmd = new NpgsqlCommand(command, db, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Exec("CREATE SCHEMA " + schema);
        foreach (var statement in analysis.Statements) await Exec(statement.Syntax.Text);
        await Exec($"INSERT INTO {schema}.employees VALUES (1,NULL,1,'boss'),(2,1,1,'worker'),(3,9,2,'independent');");
        var rows = new List<string>();
        await using (var cmd = new NpgsqlCommand($"SELECT employee,manager FROM {schema}.v ORDER BY employee", db, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(reader.GetString(0) + "|" + (reader.IsDBNull(1) ? "-" : reader.GetString(1)));
        Assert.Equal(new[] { "boss|-", "independent|-", "worker|boss" }, rows);
        foreach (var column in new[] { "id", "manager_id", "name" })
        {
            await Exec("SAVEPOINT sj_guard");
            await Assert.ThrowsAsync<PostgresException>(() => Exec(
                $"ALTER TABLE {schema}.employees DROP COLUMN {column};"));
            await Exec("ROLLBACK TO SAVEPOINT sj_guard");
        }
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }
}
