using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Actual PostgreSQL execution parity: identical source rows must be inserted
/// by the server's native WITH INSERT and by the validated F# compiler output.
/// Both destinations are session-private temporary tables.
/// </summary>
public sealed class SqlCorePostgresWithInsertLiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyCteInsertSelect_CompiledMutationMatchesNativePostgres(bool multipleCtes)
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required for live PostgreSQL parity.");
            Assert.Skip("Live PostgreSQL parity requires TEST_POSTGRES_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);

        await ExecuteAsync(connection,
            "CREATE TEMP TABLE cte_insert_parity_source(id INTEGER PRIMARY KEY);" +
            "CREATE TEMP TABLE cte_insert_parity_native(id INTEGER PRIMARY KEY);" +
            "CREATE TEMP TABLE cte_insert_parity_compiled(id INTEGER PRIMARY KEY);" +
            "INSERT INTO cte_insert_parity_source(id) VALUES (1), (2), (3);",
            token);

        var ctes = multipleCtes
            ? "WITH input_rows AS (SELECT id FROM cte_insert_parity_source WHERE id >= 2), " +
              "chosen_rows AS (SELECT id FROM input_rows) "
            : "WITH chosen_rows AS (SELECT id FROM cte_insert_parity_source WHERE id >= 2) ";
        string statement(string table) => ctes +
            "INSERT INTO " + table + "(id) SELECT id FROM chosen_rows";

        await ExecuteAsync(connection, statement("cte_insert_parity_native"), token);
        var compiled = SqlCoreFacade.CompileDml(
            statement("cte_insert_parity_compiled"),
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "postgres-with-insert-live-parity",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "cte_insert_parity_compiled", "cte_insert_parity_source"
                }));

        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, 2));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = compiled.Sql;
            foreach (var binding in compiled.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = binding.Name;
                parameter.Value = binding.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            await command.ExecuteNonQueryAsync(token);
        }

        var native = await ReadIdsAsync(connection, "cte_insert_parity_native", token);
        var actual = await ReadIdsAsync(connection, "cte_insert_parity_compiled", token);
        Assert.Equal(new[] { 2, 3 }, native);
        Assert.Equal(native, actual);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> ReadIdsAsync(DbConnection connection, string table, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM " + table + " ORDER BY id";
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            ids.Add(reader.GetInt32(0));
        return ids.ToArray();
    }
}
