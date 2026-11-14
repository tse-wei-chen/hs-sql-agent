using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Execute actual MySQL DELETE before comparing with the independently
/// parameterized F# Mutation command and untouched physical records.
/// </summary>
public sealed class SqlCoreMySqlWithDeleteLiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAndTypedWithDeleteHaveIdenticalEffects(bool multipleCtes)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_MYSQL_CONNECTION is required for real MySQL CTE DELETE parity.");
            Assert.Skip("Real MySQL parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        var database = "sqlcore_cte_delete_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(cs);
        await admin.OpenAsync(token);
        await Execute(admin, "CREATE DATABASE `" + database + "`", [], token);
        try
        {
            var opts = new MySqlConnectionStringBuilder(cs) { Database = database, Pooling = false };
            await using var db = new MySqlConnection(opts.ConnectionString);
            await db.OpenAsync(token);
            await Execute(db,
                "CREATE TABLE source_ids(id INT PRIMARY KEY) ENGINE=InnoDB;" +
                "CREATE TABLE native_result(id INT PRIMARY KEY, label VARCHAR(32)) ENGINE=InnoDB;" +
                "CREATE TABLE compiled_result(id INT PRIMARY KEY, label VARCHAR(32)) ENGINE=InnoDB;" +
                "INSERT INTO source_ids(id) VALUES (1),(2),(3);" +
                "INSERT INTO native_result(id,label) VALUES(1,'a'),(2,'b'),(3,'c');" +
                "INSERT INTO compiled_result(id,label) VALUES(1,'a'),(2,'b'),(3,'c');", [], token);

            var ctes = multipleCtes
                ? "WITH first_rows AS (SELECT id FROM source_ids WHERE id >= 2), " +
                  "picked(id) AS (SELECT id FROM first_rows) "
                : "WITH picked AS (SELECT id FROM source_ids WHERE id >= 2) ";
            string Statement(string target) => ctes + "DELETE FROM " + target +
                " WHERE id IN (SELECT id FROM picked)";

            var expectedCount = await Execute(db, Statement("native_result"), [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(8, 0, 36));
            var compiled = SqlCoreFacade.CompileDml(Statement("compiled_result"),
                SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("mysql-with-delete-live",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "source_ids", "compiled_result" }),
                new DmlCompilationPolicy(), profile, profile,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
            Assert.Equal(SqlStatementKind.Delete, compiled.Kind);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
            var actualCount = await Execute(db, compiled.Sql, compiled.Parameters, token);
            Assert.Equal(2, expectedCount);
            Assert.Equal(expectedCount, actualCount);

            var nativeRows = await Snapshot(db,
                "SELECT id,label FROM native_result ORDER BY id", token);
            var compiledRows = await Snapshot(db,
                "SELECT id,label FROM compiled_result ORDER BY id", token);
            Assert.Equal(new[] { "1:a" }, nativeRows);
            Assert.Equal(nativeRows, compiledRows);
        }
        finally
        {
            await Execute(admin, "DROP DATABASE IF EXISTS `" + database + "`", [], token);
        }
    }

    private static async Task<int> Execute(MySqlConnection db, string sql,
        IEnumerable<SqlParameterValue> bindings, CancellationToken token)
    {
        await using var command = new MySqlCommand(sql, db);
        foreach (var binding in bindings)
            command.Parameters.AddWithValue(binding.Name, binding.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string[]> Snapshot(MySqlConnection db, string sql,
        CancellationToken token)
    {
        await using var command = new MySqlCommand(sql, db);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetInt32(0) + ":" + reader.GetString(1));
        return result.ToArray();
    }
}
