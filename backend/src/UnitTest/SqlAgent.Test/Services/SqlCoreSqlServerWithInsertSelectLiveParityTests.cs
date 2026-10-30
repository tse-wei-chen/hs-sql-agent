using System.Collections.Immutable;
using System.Globalization;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Native SQL Server 2022 top-level WITH INSERT SELECT versus closed F#
/// SQLCore output and parameterized execution on actual physical tables.
/// SqlCore stays driver-free; this is an isolated integration test.
/// </summary>
public sealed class SqlCoreSqlServerWithInsertSelectLiveParityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NonrecursiveCteInsertSelectPreservesRowsAndOutput(bool multipleCtes, bool output)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server CTE INSERT parity requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server CTE INSERT live parity requires TEST_SQLSERVER_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "hctei_" + suffix + "_s";
        var nativeTarget = "hctei_" + suffix + "_n";
        var compiledTarget = "hctei_" + suffix + "_c";

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
            await Execute(db, "CREATE TABLE " + source + " (id INT NOT NULL PRIMARY KEY)", [], token);
            created.Add(source);
            foreach (var target in new[] { nativeTarget, compiledTarget })
            {
                await Execute(db, "CREATE TABLE " + target +
                    " (id INT NOT NULL PRIMARY KEY, label VARCHAR(32) NOT NULL)", [], token);
                created.Add(target);
                await Execute(db, "INSERT INTO " + target +
                    " (id,label) VALUES (0,'original')", [], token);
            }
            await Execute(db, "INSERT INTO " + source + " (id) VALUES (1),(2),(3),(4)", [], token);

            var withClause = multipleCtes
                ? "WITH seed AS (SELECT id FROM " + source + " WHERE id >= 2), " +
                  "picked(id) AS (SELECT id FROM seed WHERE id <= 3) "
                : "WITH picked AS (SELECT id FROM " + source +
                  " WHERE id >= 2 AND id <= 3) ";
            string statement(string target) => withClause +
                "INSERT INTO " + target + " (id,label) " +
                (output ? "OUTPUT INSERTED.id " : "") +
                "SELECT id, 'copied' FROM picked";

            int[] nativeOutput = output
                ? await OutputRows(db, statement(nativeTarget), [], token)
                : [];
            var nativeCount = output
                ? -1
                : await Execute(db, statement(nativeTarget), [], token);

            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var compiled = SqlCoreFacade.CompileDml(statement(compiledTarget),
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("sqlserver-cte-insert-live",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { source, compiledTarget }),
                new DmlCompilationPolicy(), profile, profile,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

            Assert.Equal(SqlStatementKind.Insert, compiled.Kind);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("INSERT INTO ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p => p.Value is not string && Convert.ToInt32(p.Value, CultureInfo.InvariantCulture) == 2);
            Assert.Contains(compiled.Parameters, p => p.Value is not string && Convert.ToInt32(p.Value, CultureInfo.InvariantCulture) == 3);
            Assert.Contains(compiled.Parameters, p => Equals(p.Value, "copied"));
            Assert.DoesNotContain("'copied'", compiled.Sql, StringComparison.OrdinalIgnoreCase);

            int[] compiledOutput = output
                ? await OutputRows(db, compiled.Sql, compiled.Parameters, token)
                : [];
            var compiledCount = output
                ? -1
                : await Execute(db, compiled.Sql, compiled.Parameters, token);
            if (output)
            {
                Assert.Equal(new[] { 2, 3 }, nativeOutput.OrderBy(x => x));
                Assert.Equal(nativeOutput.OrderBy(x => x), compiledOutput.OrderBy(x => x));
            }
            else
            {
                Assert.Equal(2, nativeCount);
                Assert.Equal(nativeCount, compiledCount);
            }
            var nativeRows = await Snapshot(db, nativeTarget, token);
            Assert.Equal(new[] { "0:original", "2:copied", "3:copied" }, nativeRows);
            Assert.Equal(nativeRows, await Snapshot(db, compiledTarget, token));
        }
        finally
        {
            foreach (var table in Enumerable.Reverse(created))
                await Execute(db, "DROP TABLE " + table, [], token);
        }
    }

    private static async Task<int> Execute(SqlConnection db, string sql,
        IEnumerable<SqlParameterValue> bindings, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, db);
        foreach (var p in bindings)
            command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> OutputRows(SqlConnection db, string sql,
        IEnumerable<SqlParameterValue> bindings, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, db);
        foreach (var p in bindings)
            command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        var rows = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(reader.GetInt32(0));
        return rows.ToArray();
    }

    private static async Task<string[]> Snapshot(SqlConnection db, string target, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT id,label FROM " + target + " ORDER BY id", db);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            values.Add(reader.GetInt32(0).ToString(CultureInfo.InvariantCulture) + ":" +
                       reader.GetString(1));
        return values.ToArray();
    }
}
