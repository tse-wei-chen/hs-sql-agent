using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Actual SQLite writes and RETURNING rows from native read-only CTE DELETE
/// must agree with SQL Core's typed, parameterized Mutation renderer.
/// </summary>
public sealed class SqlCoreSqliteWithDeleteLiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAndCompiledWithDeleteHaveIdenticalEffects(bool multipleCtes)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        await Execute(connection,
            "CREATE TABLE source_ids(id INTEGER PRIMARY KEY);" +
            "CREATE TABLE native_result(id INTEGER PRIMARY KEY, label TEXT);" +
            "CREATE TABLE compiled_result(id INTEGER PRIMARY KEY, label TEXT);" +
            "INSERT INTO source_ids(id) VALUES (1),(2),(3);" +
            "INSERT INTO native_result(id,label) VALUES(1,'a'),(2,'b'),(3,'c');" +
            "INSERT INTO compiled_result(id,label) VALUES(1,'a'),(2,'b'),(3,'c');", token);

        var ctes = multipleCtes
            ? "WITH first_rows AS (SELECT id FROM source_ids WHERE id >= 2), " +
              "picked(id) AS (SELECT id FROM first_rows) "
            : "WITH picked AS (SELECT id FROM source_ids WHERE id >= 2) ";
        string statement(string target) => ctes +
            "DELETE FROM " + target + " WHERE id IN (SELECT id FROM picked) RETURNING id";

        var expected = await Read(connection, statement("native_result"),
            Array.Empty<SqlParameterValue>(), token);

        var version = Version.Parse(connection.ServerVersion);
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, version);
        var compiled = SqlCoreFacade.CompileDml(statement("compiled_result"),
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-cte-delete-live",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "source_ids", "compiled_result" }),
            new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

        Assert.Equal(SqlStatementKind.Delete, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
        var actual = await Read(connection, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(new[] { 2, 3 }, expected.OrderBy(x => x));
        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));

        var remainingNative = await Read(connection,
            "SELECT id FROM native_result ORDER BY id", Array.Empty<SqlParameterValue>(), token);
        var remainingCompiled = await Read(connection,
            "SELECT id FROM compiled_result ORDER BY id", Array.Empty<SqlParameterValue>(), token);
        Assert.Equal(new[] { 1 }, remainingNative);
        Assert.Equal(remainingNative, remainingCompiled);
    }

    private static async Task Execute(DbConnection db, string sql, CancellationToken token)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> Read(DbConnection db, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var command = db.CreateCommand();
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
