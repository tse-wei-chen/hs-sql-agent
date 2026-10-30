using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// In-memory real SQLite parity. Compares actual writes and RETURNING rows
/// of native CTE INSERT and the existing parameterized F# Mutation compiler.
/// This exercises the provider grammar, not merely a rendered SQL snapshot.
/// </summary>
public sealed class SqlCoreSqliteWithInsertLiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAndCompiledSqliteCteInsertSelectProduceIdenticalRows(bool multipleCtes)
    {
        var token = TestContext.Current.CancellationToken;
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(token);
        await ExecuteAsync(sqlite,
            "CREATE TABLE source_ids(id INTEGER PRIMARY KEY);" +
            "CREATE TABLE native_result(id INTEGER PRIMARY KEY);" +
            "CREATE TABLE compiled_result(id INTEGER PRIMARY KEY);" +
            "INSERT INTO source_ids(id) VALUES(1), (2), (3);", token);

        var ctes = multipleCtes
            ? "WITH first_rows AS (SELECT id FROM source_ids WHERE id >= 2), " +
              "input_rows AS (SELECT id FROM first_rows) "
            : "WITH input_rows AS (SELECT id FROM source_ids WHERE id >= 2) ";
        string stmt(string table) => ctes +
            "INSERT INTO " + table + "(id) SELECT id FROM input_rows RETURNING id";

        var expected = await ReadAsync(sqlite, stmt("native_result"),
            Array.Empty<SqlParameterValue>(), token);
        var compiled = Compile(stmt("compiled_result"), Version.Parse(sqlite.ServerVersion));
        var actual = await ReadAsync(sqlite, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(new[] { 2, 3 }, expected);
        Assert.Equal(expected, actual);
        Assert.Equal(await ReadAsync(sqlite, "SELECT id FROM native_result ORDER BY id",
                Array.Empty<SqlParameterValue>(), token),
            await ReadAsync(sqlite, "SELECT id FROM compiled_result ORDER BY id",
                Array.Empty<SqlParameterValue>(), token));
    }

    [Fact]
    public async Task SQLiteConflictDoNothingWithSelectWhereMatchesNativeReturningAndRows()
    {
        var token = TestContext.Current.CancellationToken;
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(token);
        await ExecuteAsync(sqlite,
            "CREATE TABLE source_ids(id INTEGER PRIMARY KEY);" +
            "CREATE TABLE native_result(id INTEGER PRIMARY KEY);" +
            "CREATE TABLE compiled_result(id INTEGER PRIMARY KEY);" +
            "INSERT INTO source_ids(id) VALUES(17), (23);" +
            "INSERT INTO native_result(id) VALUES(17);" +
            "INSERT INTO compiled_result(id) VALUES(17);", token);

        const string cte = "WITH input_rows AS (SELECT id FROM source_ids) ";
        string stmt(string table) =>
            cte + "INSERT INTO " + table + "(id) SELECT id FROM input_rows " +
            "WHERE id > 0 ON CONFLICT(id) DO NOTHING RETURNING id";

        var expected = await ReadAsync(sqlite, stmt("native_result"),
            Array.Empty<SqlParameterValue>(), token);
        var compiled = Compile(stmt("compiled_result"), Version.Parse(sqlite.ServerVersion));
        Assert.Contains("ON CONFLICT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        var actual = await ReadAsync(sqlite, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(new[] { 23 }, expected);
        Assert.Equal(expected, actual);
        Assert.Equal(new[] { 17, 23 },
            await ReadAsync(sqlite, "SELECT id FROM compiled_result ORDER BY id",
                Array.Empty<SqlParameterValue>(), token));
    }

    private static CompiledSqlCommand Compile(string sql, Version runtimeVersion)
    {
        // Version is proven from this opened SQLite connection, not assumed.
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, runtimeVersion);
        return SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-with-insert-live-parity",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "source_ids", "compiled_result"
                }),
            new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> ReadAsync(DbConnection connection, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var binding in parameters)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = binding.Name;
            parameter.Value = binding.Value ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            ids.Add(reader.GetInt32(0));
        return ids.ToArray();
    }
}
