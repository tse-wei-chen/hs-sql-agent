using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real PostgreSQL execution comparison, not just SQL-text parity.
/// Native WITH UPDATE and the validated F# mutation must affect the
/// identical bounded row set and return the same identities.
/// </summary>
public sealed class SqlCorePostgresWithUpdateLiveParityTests
{
    [Fact]
    public async Task NativeAndCompiledWithUpdateModifyExactlySameRows()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required for live WITH UPDATE parity.");
            Assert.Skip("Live PostgreSQL parity requires TEST_POSTGRES_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);

        await ExecuteAsync(connection,
            "CREATE TEMP TABLE sqlcore_with_update_source (id INTEGER PRIMARY KEY);" +
            "CREATE TEMP TABLE sqlcore_with_update_native (id INTEGER PRIMARY KEY, label TEXT);" +
            "CREATE TEMP TABLE sqlcore_with_update_compiled (id INTEGER PRIMARY KEY, label TEXT);" +
            "INSERT INTO sqlcore_with_update_source(id) VALUES (1), (2), (3);" +
            "INSERT INTO sqlcore_with_update_native(id,label) VALUES (1,'old'),(2,'old'),(3,'old');" +
            "INSERT INTO sqlcore_with_update_compiled(id,label) VALUES (1,'old'),(2,'old'),(3,'old');",
            token);

        string Sql(string target) =>
            "WITH picked AS (SELECT id FROM sqlcore_with_update_source WHERE id = 2) " +
            "UPDATE " + target + " SET label = 'changed' " +
            "FROM picked WHERE " + target + ".id = picked.id " +
            "RETURNING " + target + ".id";

        var native = await ReadIdsAsync(connection,
            Sql("sqlcore_with_update_native"), Array.Empty<SqlParameterValue>(), token);

        var compiled = SqlCoreFacade.CompileDml(
            Sql("sqlcore_with_update_compiled"),
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-cte-update-live-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "sqlcore_with_update_source", "sqlcore_with_update_compiled" }));
        Assert.Equal(SqlStatementKind.Update, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "changed"));
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));

        var actual = await ReadIdsAsync(connection, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(new[] { 2 }, native);
        Assert.Equal(native, actual);

        var expected = new[] { "1:old", "2:changed", "3:old" };
        Assert.Equal(expected,
            await ReadStateAsync(connection, "sqlcore_with_update_native", token));
        Assert.Equal(expected,
            await ReadStateAsync(connection, "sqlcore_with_update_compiled", token));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> ReadIdsAsync(DbConnection connection, string sql,
        IReadOnlyCollection<SqlParameterValue> bindings, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var binding in bindings)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = binding.Name;
            parameter.Value = binding.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) ids.Add(reader.GetInt32(0));
        return ids.ToArray();
    }

    private static async Task<string[]> ReadStateAsync(DbConnection connection,
        string table, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, label FROM " + table + " ORDER BY id";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(reader.GetInt32(0) + ":" + reader.GetString(1));
        return rows.ToArray();
    }
}
