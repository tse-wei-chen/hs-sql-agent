using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real native PostgreSQL versus closed F# Mutation compiler parity:
/// existing duplicate rows must be skipped and RETURNING must report only
/// the newly inserted rows. Uses two isolated temporary tables.
/// </summary>
public sealed class SqlCorePostgresWithInsertModifiersLiveParityTests
{
    [Fact]
    public async Task CteInsertConflictDoNothingReturningMatchesNativePostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "TEST_POSTGRES_CONNECTION is required for WITH INSERT modifier parity.");
            Assert.Skip("Live PostgreSQL parity requires TEST_POSTGRES_CONNECTION.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection,
            "CREATE TEMP TABLE sqlcore_mod_native(id INTEGER PRIMARY KEY);" +
            "CREATE TEMP TABLE sqlcore_mod_compiled(id INTEGER PRIMARY KEY);" +
            "INSERT INTO sqlcore_mod_native(id) VALUES (17);" +
            "INSERT INTO sqlcore_mod_compiled(id) VALUES (17);",
            cancellationToken);

        const string cte = "WITH source_rows AS (" +
                           "SELECT 17 AS id UNION ALL SELECT 23 AS id) ";
        string statement(string table) =>
            cte + "INSERT INTO " + table + "(id) " +
            "SELECT id FROM source_rows WHERE id > 0 " +
            "ON CONFLICT (id) DO NOTHING RETURNING id";

        var native = await ReadIdsAsync(connection, statement("sqlcore_mod_native"),
            Array.Empty<SqlParameterValue>(), cancellationToken);

        var compiled = SqlCoreFacade.CompileDml(
            statement("sqlcore_mod_compiled"),
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-with-insert-modifier-parity",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "sqlcore_mod_compiled" }));
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON CONFLICT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RETURNING", compiled.Sql, StringComparison.OrdinalIgnoreCase);

        var actual = await ReadIdsAsync(connection, compiled.Sql,
            compiled.Parameters, cancellationToken);
        Assert.Equal(new[] { 23 }, native);
        Assert.Equal(native, actual);
        Assert.Equal(new[] { 17, 23 },
            await ReadIdsAsync(connection, "SELECT id FROM sqlcore_mod_compiled ORDER BY id",
                Array.Empty<SqlParameterValue>(), cancellationToken));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int[]> ReadIdsAsync(DbConnection connection, string sql,
        IReadOnlyCollection<SqlParameterValue> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var binding in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = binding.Name;
            parameter.Value = binding.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetInt32(0));
        return result.ToArray();
    }
}
