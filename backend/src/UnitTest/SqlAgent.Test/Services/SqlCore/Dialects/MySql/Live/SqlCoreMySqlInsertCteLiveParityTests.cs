using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Native-vs-compiled MySQL 8 INSERT ... WITH ... SELECT execution parity.
/// SQLCore itself remains driver-free; only this test connects to MySQL.
/// </summary>
public sealed class SqlCoreMySqlInsertCteLiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MySqlNativeAndCompiledCteInsertProduceIdenticalRows(bool multipleCtes)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_MYSQL_CONNECTION is required for real MySQL CTE INSERT parity.");
            Assert.Skip("Real MySQL parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        var database = "sqlcore_cte_insert_" + Guid.NewGuid().ToString("N");
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
                "INSERT INTO source_ids(id) VALUES (1),(2),(3);", [], token);

            var ctes = multipleCtes
                ? "WITH seed AS (SELECT id FROM source_ids WHERE id >= 2), " +
                  "picked(id) AS (SELECT id FROM seed) "
                : "WITH picked AS (SELECT id FROM source_ids WHERE id >= 2) ";
            string statement(string target) =>
                "INSERT INTO " + target + "(id,label) " + ctes +
                "SELECT id, 'copied' FROM picked";

            var nativeRows = await Execute(connection, statement("native_result"), [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(8, 0, 36));
            var compiled = SqlCoreFacade.CompileDml(statement("compiled_result"),
                SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("mysql-cte-insert-live",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "source_ids", "compiled_result" }),
                new DmlCompilationPolicy(), profile, profile,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

            Assert.Equal(SqlStatementKind.Insert, compiled.Kind);
            Assert.StartsWith("INSERT INTO", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
            Assert.Contains(compiled.Parameters, p => Equals(p.Value, "copied"));
            var compiledRows = await Execute(connection, compiled.Sql, compiled.Parameters, token);
            Assert.Equal(2, nativeRows);
            Assert.Equal(nativeRows, compiledRows);
            var expected = await Snapshot(connection, "SELECT id,label FROM native_result ORDER BY id", token);
            var actual = await Snapshot(connection, "SELECT id,label FROM compiled_result ORDER BY id", token);
            Assert.Equal(new[] { "2:copied", "3:copied" }, expected);
            Assert.Equal(expected, actual);
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
