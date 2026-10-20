using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Compiles each PostgreSQL/SQLite/MySQL source into all three target dialects,
/// then compares actual ordered result rows against native PostgreSQL.
/// These are true execution-result tests, not SQL-string comparisons.
/// </summary>
public sealed class SqlCoreThreeProviderExecutionParityTests
{
    private static readonly string[] PortableQueries =
    [
        "SELECT id, name FROM sqlcore_parity_users WHERE id >= 2 ORDER BY id",
        "WITH filtered AS (SELECT id, name FROM sqlcore_parity_users WHERE id >= 2) " +
            "SELECT id, name FROM filtered ORDER BY id",
        "SELECT id FROM sqlcore_parity_users WHERE name IS NULL ORDER BY id",
        "SELECT id FROM sqlcore_parity_users " +
            "WHERE id BETWEEN 1 AND 4 AND id <> 2 ORDER BY id",
        "SELECT id FROM sqlcore_parity_users WHERE name LIKE 'B%' ORDER BY id",
        "SELECT id FROM sqlcore_parity_users WHERE id IN (1, 3) ORDER BY id",
        "SELECT id, name FROM sqlcore_parity_users ORDER BY id LIMIT 3"
    ];

    [Fact]
    public async Task ThreeProviders_ExecuteEquivalentNativeAndCrossDialectQueries()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "TEST_POSTGRES_CONNECTION must be supplied to run real-provider differential tests in CI.");

            Assert.Skip("Real PostgreSQL parity requires TEST_POSTGRES_CONNECTION (SQLite runs in memory).");
        }

        var mysqlConnectionString = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "TEST_MYSQL_CONNECTION must be supplied for real-provider matrix execution in CI.");
            Assert.Skip("Real MySQL parity requires TEST_MYSQL_CONNECTION.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = new NpgsqlConnection(connectionString);
        await postgres.OpenAsync(cancellationToken);
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(cancellationToken);
        await using var mysql = new MySqlConnection(new MySqlConnectionStringBuilder(mysqlConnectionString) { Pooling = false }.ConnectionString);
        await mysql.OpenAsync(cancellationToken);

        await ExecuteAsync(postgres,
            "CREATE TEMP TABLE sqlcore_parity_users (id INTEGER PRIMARY KEY, name TEXT NULL)",
            cancellationToken);
        await ExecuteAsync(sqlite,
            "CREATE TABLE sqlcore_parity_users (id INTEGER PRIMARY KEY, name TEXT NULL)",
            cancellationToken);
        await ExecuteAsync(mysql,
            "CREATE TEMPORARY TABLE sqlcore_parity_users (id INT PRIMARY KEY, name TEXT NULL)",
            cancellationToken);
        const string seed = "INSERT INTO sqlcore_parity_users (id, name) " +
            "VALUES (1, 'Ada'), (2, 'Bea'), (3, NULL), (4, 'Dana')";
        await ExecuteAsync(postgres, seed, cancellationToken);
        await ExecuteAsync(sqlite, seed, cancellationToken);
        await ExecuteAsync(mysql, seed, cancellationToken);

        var providers = new[]
        {
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.MySQL
        };
        DbConnection TargetConnection(SqlAgentToolType target) => target switch
        {
            SqlAgentToolType.Postgres => postgres,
            SqlAgentToolType.Sqlite => sqlite,
            SqlAgentToolType.MySQL => mysql,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        var comparisons = 0;
        foreach (var sql in PortableQueries)
        {
            var expected = await ReadRowsAsync(
                postgres,
                Compile(sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres),
                cancellationToken);

            foreach (var source in providers)
            {
                foreach (var target in providers)
                {
                    var compiled = Compile(sql, source, target);
                    var rows = await ReadRowsAsync(
                        TargetConnection(target), compiled, cancellationToken);
                    Assert.Equal(expected, rows);
                    comparisons++;
                }
            }
        }

        Assert.Equal(7, PortableQueries.Length);
        Assert.Equal(63, comparisons);
    }

    private static CompiledSqlCommand Compile(
        string sql, SqlAgentToolType source, SqlAgentToolType target)
    {
        var snapshot = new SqlSchemaSnapshot(source, "parity-fixture-v1",
        [
            SqlSchemaTable.WithTypes("sqlcore_parity_users",
            [
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
            ])
        ]);

        return SqlCoreFacade.CompileQuery(
            sql, source, target,
            new SqlPlanValidationContext("parity-fixture-policy-v1").WithSchemaSnapshot(snapshot),
            new SqlExecutionPlanPolicy(0));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<List<string>> ReadRowsAsync(
        DbConnection connection,
        CompiledSqlCommand compiled,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        foreach (var binding in compiled.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = binding.Name;
            parameter.Value = binding.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var values = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i)
                    ? null
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            // JSON preserves column order and NULL distinct from empty string.
            rows.Add(JsonSerializer.Serialize(values));
        }

        return rows;
    }
}
