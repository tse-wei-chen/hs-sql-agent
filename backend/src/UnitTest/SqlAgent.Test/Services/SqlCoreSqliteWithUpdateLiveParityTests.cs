using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real SQLite update effects and RETURNING rows must match the compiled
/// parameterized command, not merely pass the SQL Core AST/parser.
/// </summary>
public sealed class SqlCoreSqliteWithUpdateLiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAndCompiledWithUpdateHaveIdenticalEffects(bool multipleCtes)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        await Execute(connection,
            "CREATE TABLE source_ids(id INTEGER PRIMARY KEY);" +
            "CREATE TABLE native_result(id INTEGER PRIMARY KEY, label TEXT);" +
            "CREATE TABLE compiled_result(id INTEGER PRIMARY KEY, label TEXT);" +
            "INSERT INTO source_ids(id) VALUES (1),(2),(3);" +
            "INSERT INTO native_result(id,label) VALUES(1,'old'),(2,'old'),(3,'old');" +
            "INSERT INTO compiled_result(id,label) VALUES(1,'old'),(2,'old'),(3,'old');", token);

        var ctes = multipleCtes
            ? "WITH first_rows AS (SELECT id FROM source_ids WHERE id >= 2), " +
              "picked(id) AS (SELECT id FROM first_rows) "
            : "WITH picked AS (SELECT id FROM source_ids WHERE id >= 2) ";
        string statement(string target) => ctes +
            "UPDATE " + target + " SET label = 'changed' " +
            "WHERE id IN (SELECT id FROM picked) RETURNING id";

        var expected = await Read(connection, statement("native_result"),
            Array.Empty<SqlParameterValue>(), token);
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite,
            Version.Parse(connection.ServerVersion));
        var compiled = SqlCoreFacade.CompileDml(statement("compiled_result"),
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-cte-update-live",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "source_ids", "compiled_result" }),
            new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

        Assert.Equal(SqlStatementKind.Update, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, 2));
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, "changed"));
        var actual = await Read(connection, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(new[] { 2, 3 }, expected.OrderBy(x => x));
        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));

        var nativeFinal = await Read(connection,
            "SELECT id FROM native_result WHERE label = 'changed' ORDER BY id",
            Array.Empty<SqlParameterValue>(), token);
        var compiledFinal = await Read(connection,
            "SELECT id FROM compiled_result WHERE label = 'changed' ORDER BY id",
            Array.Empty<SqlParameterValue>(), token);
        Assert.Equal(nativeFinal, compiledFinal);

        // Comparing only updated/RETURNING rows can miss unintended writes
        // to rows outside the intended predicate. Prove the entire table.
        var nativeSnapshot = await ReadSnapshot(connection,
            "SELECT id,label FROM native_result ORDER BY id", token);
        var compiledSnapshot = await ReadSnapshot(connection,
            "SELECT id,label FROM compiled_result ORDER BY id", token);
        Assert.Equal(new[] { "1:old", "2:changed", "3:changed" }, nativeSnapshot);
        Assert.Equal(nativeSnapshot, compiledSnapshot);
    }

    private static async Task<string[]> ReadSnapshot(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(reader.GetInt32(0) + ":" + reader.GetString(1));
        return rows.ToArray();
    }

    private static async Task Execute(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> Read(DbConnection connection, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
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
        var rows = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(reader.GetInt32(0));
        return rows.ToArray();
    }
}
