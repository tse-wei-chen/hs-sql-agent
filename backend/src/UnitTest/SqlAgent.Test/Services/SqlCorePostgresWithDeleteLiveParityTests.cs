using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Native PostgreSQL and typed SQLCore DELETE must remove the same bounded
/// row and RETURNING result without changing other rows.
/// </summary>
public sealed class SqlCorePostgresWithDeleteLiveParityTests
{
    [Fact]
    public async Task NativeAndCompiledWithDeleteMatchResultAndRemainingRows()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required for live WITH DELETE parity.");
            Assert.Skip("Live PostgreSQL parity requires TEST_POSTGRES_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(token);
        await Execute(conn,
            "CREATE TEMP TABLE sqlcore_delete_source(id INTEGER PRIMARY KEY);" +
            "CREATE TEMP TABLE sqlcore_delete_native(id INTEGER PRIMARY KEY, label TEXT);" +
            "CREATE TEMP TABLE sqlcore_delete_compiled(id INTEGER PRIMARY KEY, label TEXT);" +
            "INSERT INTO sqlcore_delete_source(id) VALUES(1),(2),(3);" +
            "INSERT INTO sqlcore_delete_native(id,label) VALUES(1,'a'),(2,'b'),(3,'c');" +
            "INSERT INTO sqlcore_delete_compiled(id,label) VALUES(1,'a'),(2,'b'),(3,'c');",
            token);

        string Sql(string target) =>
            "WITH picked AS (SELECT id FROM sqlcore_delete_source WHERE id = 2) " +
            "DELETE FROM " + target + " USING picked p WHERE " + target + ".id = p.id " +
            "RETURNING " + target + ".id";

        var native = await Read(conn, Sql("sqlcore_delete_native"), Array.Empty<SqlParameterValue>(), token);
        var compiled = SqlCoreFacade.CompileDml(
            Sql("sqlcore_delete_compiled"),
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-with-delete-live-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "sqlcore_delete_source", "sqlcore_delete_compiled" }));
        Assert.Equal(SqlStatementKind.Delete, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
        var actual = await Read(conn, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(new[] { 2 }, native);
        Assert.Equal(native, actual);
        var expectedRemaining = new[] { 1, 3 };
        Assert.Equal(expectedRemaining,
            await Read(conn, "SELECT id FROM sqlcore_delete_native ORDER BY id", Array.Empty<SqlParameterValue>(), token));
        Assert.Equal(expectedRemaining,
            await Read(conn, "SELECT id FROM sqlcore_delete_compiled ORDER BY id", Array.Empty<SqlParameterValue>(), token));
    }

    private static async Task Execute(DbConnection connection, string sql, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> Read(DbConnection connection, string sql,
        IReadOnlyCollection<SqlParameterValue> parameters, CancellationToken token)
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
        while (await reader.ReadAsync(token)) ids.Add(reader.GetInt32(0));
        return ids.ToArray();
    }
}
