using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real MySQL 8+ native-vs-compiled UPDATE row count and full-table parity;
/// independent actual SQL execution is test-only, never part of SqlCore.
/// </summary>
public sealed class SqlCoreMySqlWithUpdateLiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MySqlNativeAndTypedUpdateModifyTheSameRows(bool multipleCtes)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_MYSQL_CONNECTION is required for real MySQL CTE UPDATE parity.");
            Assert.Skip("Real MySQL parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        var database = "sqlcore_cte_update_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(cs);
        await admin.OpenAsync(token);
        await Execute(admin, "CREATE DATABASE `" + database + "`", [], token);
        try
        {
            var options = new MySqlConnectionStringBuilder(cs)
            {
                Database = database,
                Pooling = false
            };
            await using var connection = new MySqlConnection(options.ConnectionString);
            await connection.OpenAsync(token);
            await Execute(connection,
                "CREATE TABLE source_ids(id INT PRIMARY KEY) ENGINE=InnoDB;" +
                "CREATE TABLE native_result(id INT PRIMARY KEY, label VARCHAR(32)) ENGINE=InnoDB;" +
                "CREATE TABLE compiled_result(id INT PRIMARY KEY, label VARCHAR(32)) ENGINE=InnoDB;" +
                "INSERT INTO source_ids(id) VALUES (1),(2),(3);" +
                "INSERT INTO native_result(id,label) VALUES (1,'old'),(2,'old'),(3,'old');" +
                "INSERT INTO compiled_result(id,label) VALUES (1,'old'),(2,'old'),(3,'old');", [], token);

            var ctes = multipleCtes
                ? "WITH first_rows AS (SELECT id FROM source_ids WHERE id >= 2), " +
                  "picked(id) AS (SELECT id FROM first_rows) "
                : "WITH picked AS (SELECT id FROM source_ids WHERE id >= 2) ";
            string statement(string target) => ctes +
                "UPDATE " + target + " SET label = 'changed' " +
                "WHERE id IN (SELECT id FROM picked)";

            var expectedCount = await Execute(connection, statement("native_result"), [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(8, 0, 36));
            var compiled = SqlCoreFacade.CompileDml(statement("compiled_result"),
                SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("mysql-cte-update-live",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "source_ids", "compiled_result" }),
                new DmlCompilationPolicy(), profile, profile,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

            Assert.Equal(SqlStatementKind.Update, compiled.Kind);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
            Assert.Contains(compiled.Parameters, p => Equals(p.Value, "changed"));
            var actualCount = await Execute(connection, compiled.Sql, compiled.Parameters, token);
            Assert.Equal(2, expectedCount);
            Assert.Equal(expectedCount, actualCount);

            var expectedRows = await Snapshot(connection,
                "SELECT id,label FROM native_result ORDER BY id", token);
            var actualRows = await Snapshot(connection,
                "SELECT id,label FROM compiled_result ORDER BY id", token);
            Assert.Equal(new[] { "1:old", "2:changed", "3:changed" }, expectedRows);
            Assert.Equal(expectedRows, actualRows);
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + database + "`", [], token);
        }
    }

    private static async Task<int> Execute(MySqlConnection connection, string sql,
        IEnumerable<SqlParameterValue> bindings, CancellationToken token)
    {
        await using var command = new MySqlCommand(sql, connection);
        foreach (var binding in bindings)
            command.Parameters.AddWithValue(binding.Name, binding.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string[]> Snapshot(MySqlConnection connection, string sql,
        CancellationToken token)
    {
        await using var command = new MySqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(reader.GetInt32(0) + ":" + reader.GetString(1));
        return rows.ToArray();
    }
}
