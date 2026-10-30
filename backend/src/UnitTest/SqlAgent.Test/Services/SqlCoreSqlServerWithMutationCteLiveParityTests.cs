using System.Collections.Immutable;
using System.Globalization;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Actual SQL Server 2022 native SQL versus strictly compiled WITH UPDATE/DELETE.
/// This is test-only database I/O: SqlCore remains driver-free.
/// </summary>
public sealed class SqlCoreSqlServerWithMutationCteLiveParityTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NonrecursiveCteMutationMatchesNativeRows(bool update, bool multipleCtes)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server CTE Mutation parity requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Real SQL Server is required for CTE Mutation parity.");
        }

        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "hcte_" + suffix + "_s";
        var nativeTarget = "hcte_" + suffix + "_n";
        var compiledTarget = "hcte_" + suffix + "_c";

        await using var db = new SqlConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }

        var created = new List<string>();
        try
        {
            await Execute(db, "CREATE TABLE " + source + " (id INT PRIMARY KEY)", [], token);
            created.Add(source);
            foreach (var target in new[] { nativeTarget, compiledTarget })
            {
                await Execute(db, "CREATE TABLE " + target +
                    " (id INT PRIMARY KEY, label VARCHAR(32) NOT NULL)", [], token);
                created.Add(target);
                await Execute(db, "INSERT INTO " + target +
                    " (id,label) VALUES (1,'old'),(2,'old'),(3,'old')", [], token);
            }
            await Execute(db, "INSERT INTO " + source + " (id) VALUES (1),(2),(3)", [], token);

            var withClause = multipleCtes
                ? "WITH seed AS (SELECT id FROM " + source + " WHERE id >= 2), " +
                  "picked(id) AS (SELECT id FROM seed) "
                : "WITH picked AS (SELECT id FROM " + source + " WHERE id >= 2) ";
            string statement(string target) => withClause +
                (update
                    ? "UPDATE " + target + " SET label = 'changed' WHERE id IN (SELECT id FROM picked)"
                    : "DELETE FROM " + target + " WHERE id IN (SELECT id FROM picked)");

            var nativeCount = await Execute(db, statement(nativeTarget), [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var compiled = SqlCoreFacade.CompileDml(statement(compiledTarget),
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("sqlserver-cte-mutation-live",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { source, compiledTarget }),
                new DmlCompilationPolicy(), profile, profile,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

            Assert.Equal(update ? SqlStatementKind.Update : SqlStatementKind.Delete, compiled.Kind);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p =>
                Convert.ToInt32(p.Value, CultureInfo.InvariantCulture) == 2);
            if (update)
                Assert.Contains(compiled.Parameters, p => Equals(p.Value, "changed"));
            Assert.DoesNotContain("'changed'", compiled.Sql, StringComparison.OrdinalIgnoreCase);

            var compiledCount = await Execute(db, compiled.Sql, compiled.Parameters, token);
            Assert.Equal(2, nativeCount);
            Assert.Equal(nativeCount, compiledCount);

            var expected = await Snapshot(db, nativeTarget, token);
            var actual = await Snapshot(db, compiledTarget, token);
            Assert.Equal(update
                ? new[] { "1:old", "2:changed", "3:changed" }
                : new[] { "1:old" }, expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            foreach (var target in Enumerable.Reverse(created))
                await Execute(db, "DROP TABLE " + target, [], token);
        }
    }

    private static async Task<int> Execute(SqlConnection db, string sql,
        IEnumerable<SqlParameterValue> bindings, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, db);
        foreach (var binding in bindings)
            command.Parameters.AddWithValue(binding.Name, binding.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string[]> Snapshot(SqlConnection db, string target, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT id,label FROM " + target + " ORDER BY id", db);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetInt32(0).ToString(CultureInfo.InvariantCulture) +
                       ":" + reader.GetString(1));
        return result.ToArray();
    }
}
