using System.Collections.Immutable;
using System.Globalization;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.Provider.SqlServer;
using SqlAgent.Service.Core.Execution;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Live SQL Server 2022 native-vs-compiled singleton CTE source cardinality.
/// A real physical UPDATE/DELETE target must be modified identically.
/// </summary>
public sealed class SqlCoreSqlServerWithJoinedMutationSingletonLiveParityTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SingletonCteJoinMatchesNativeRows(bool update, bool chained)
    {
        var connection = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server joined CTE parity requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Real SQL Server required for joined CTE mutation parity.");
        }

        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var nativeTable = "hjcte_" + suffix + "_n";
        var compiledTable = "hjcte_" + suffix + "_c";
        await using var db = new SqlConnection(connection);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }

        var created = new List<string>();
        try
        {
            foreach (var target in new[] { nativeTable, compiledTable })
            {
                await Execute(db, "CREATE TABLE " + target +
                    " (id INT NOT NULL PRIMARY KEY, label VARCHAR(32) NOT NULL)", [], token);
                created.Add(target);
                await Execute(db, "INSERT INTO " + target +
                    " (id,label) VALUES (1,'old'),(2,'old'),(3,'old')", [], token);
            }

            var ctes = chained
                ? "WITH seed AS (SELECT 2 AS id), picked(id) AS (SELECT id FROM seed WHERE id > 0) "
                : "WITH picked AS (SELECT 2 AS id) ";

            string Sql(string target) => ctes +
                (update
                    ? "UPDATE " + target +
                      " SET label = 'changed' FROM picked WHERE " + target + ".id = picked.id"
                    : "DELETE FROM " + target +
                      " FROM picked WHERE " + target + ".id = picked.id");

            var nativeAffected = await Execute(db, Sql(nativeTable), [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var compiled = SqlCoreFacade.CompileDml(Sql(compiledTable),
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("sqlserver-joined-cte-live",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { compiledTable }),
                new DmlCompilationPolicy(), profile, profile,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

            Assert.Equal(update ? SqlStatementKind.Update : SqlStatementKind.Delete, compiled.Kind);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(" FROM ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
            if (update)
                Assert.Contains(compiled.Parameters, p => Equals(p.Value, "changed"));
            Assert.DoesNotContain("SELECT 2", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            var compiledAffected = await Execute(db, compiled.Sql, compiled.Parameters, token);
            Assert.Equal(1, nativeAffected);
            Assert.Equal(nativeAffected, compiledAffected);

            var nativeRows = await Snapshot(db, nativeTable, token);
            Assert.Equal(update
                ? new[] { "1:old", "2:changed", "3:old" }
                : new[] { "1:old", "3:old" }, nativeRows);
            Assert.Equal(nativeRows, await Snapshot(db, compiledTable, token));
        }
        finally
        {
            foreach (var table in Enumerable.Reverse(created))
                await Execute(db, "DROP TABLE " + table, [], token);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostPlanUsesSameCteForApprovedMatchAndActualMutation(bool update)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server host CTE match parity requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server host CTE match parity requires TEST_SQLSERVER_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        var target = "hctehost_" + Guid.NewGuid().ToString("N")[..12];
        await using var db = new SqlConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        try
        {
            await Execute(db,
                "CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30) NOT NULL)",
                [], token);
            await Execute(db, "INSERT INTO dbo.[" + target +
                "] (id,label) VALUES (1,'old'),(2,'old'),(3,'old')", [], token);
            var sql = "WITH picked AS (SELECT 2 AS id) " +
                (update
                    ? "UPDATE dbo." + target + " SET label = 'changed' " +
                      "FROM picked WHERE " + target + ".id = picked.id"
                    : "DELETE FROM dbo." + target +
                      " FROM picked WHERE " + target + ".id = picked.id");
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, profile);
            var plan = await new DmlPlanFactory(new MsSqlServerProvider())
                .CreateWithMetadataConnectionAsync(
                    db, cs, parsed, SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext("host-cte-live-match",
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo." + target }),
                    new DmlCompilationPolicy(),
                    maxAffectedRows: 3,
                    cancellationToken: token,
                    targetProfile: profile);

            var match = Assert.IsType<CompiledSqlCommand>(plan.MatchQueryCommand);
            Assert.StartsWith("WITH ", match.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("WITH ", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
            await using (var command = new SqlCommand(match.Sql, db))
            {
                foreach (var p in match.Parameters)
                    command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
                var ids = new List<int>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    ids.Add(reader.GetInt32(0));
                Assert.Equal(new[] { 2 }, ids);
            }

            var affected = await Execute(db, plan.MutationCommand.Sql,
                plan.MutationCommand.Parameters, token);
            Assert.Equal(1, affected);
            Assert.Equal(update
                ? new[] { "1:old", "2:changed", "3:old" }
                : new[] { "1:old", "3:old" }, await Snapshot(db, target, token));
        }
        finally
        {
            await Execute(db, "DROP TABLE IF EXISTS dbo.[" + target + "]", [], token);
        }
    }

    private static async Task<int> Execute(SqlConnection db, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, db);
        foreach (var p in parameters)
            command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string[]> Snapshot(SqlConnection db, string table, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT id,label FROM " + table + " ORDER BY id", db);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetInt32(0).ToString(CultureInfo.InvariantCulture) + ":" +
                       reader.GetString(1));
        return result.ToArray();
    }
}
