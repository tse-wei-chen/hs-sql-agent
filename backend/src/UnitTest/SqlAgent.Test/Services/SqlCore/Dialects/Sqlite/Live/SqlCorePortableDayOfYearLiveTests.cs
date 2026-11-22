using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Reuses the MySQL 8.4 CI service and in-memory SQLite. Compare native
/// day-of-year against parameterized compiled output at leap-year boundaries.
/// </summary>
public sealed class SqlCorePortableDayOfYearLiveTests
{
    private const string SourceSql =
        "SELECT EXTRACT(DOY FROM DATE '2024-01-01') AS first_day, " +
        "EXTRACT(DOY FROM DATE '2024-02-29') AS leap_day, " +
        "EXTRACT(DOY FROM DATE '2024-12-31') AS last_day, " +
        "EXTRACT(DOY FROM CAST(NULL AS DATE)) AS null_day";

    [Fact]
    public async Task SqliteLive_PostgresDoyMatchesNativeLeapYearAndNull()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(token);
        var command = db.CreateCommand();
        await using (command)
        {
            var compiled = Compile(SqlAgentToolType.Sqlite);
            command.CommandText = compiled.Sql;
            foreach (var parameter in compiled.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using (var reader = await command.ExecuteReaderAsync(token))
                await AssertDayOfYear(reader, token);
        }

        await using var native = db.CreateCommand();
        native.CommandText =
            "SELECT CAST(STRFTIME('%j','2024-01-01') AS INTEGER), " +
            "CAST(STRFTIME('%j','2024-02-29') AS INTEGER), " +
            "CAST(STRFTIME('%j','2024-12-31') AS INTEGER), " +
            "CAST(STRFTIME('%j',NULL) AS INTEGER)";
        await using var reference = await native.ExecuteReaderAsync(token);
        await AssertDayOfYear(reference, token);
    }

    [Fact]
    public async Task MySql84Live_PostgresDoyMatchesNativeLeapYearAndNull()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "MySQL day-of-year parity requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("MySQL day-of-year parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var db = new MySqlConnection(
            new MySqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.MySQL);
        await using (var command = new MySqlCommand(compiled.Sql, db))
        {
            foreach (var parameter in compiled.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using (var reader = await command.ExecuteReaderAsync(token))
                await AssertDayOfYear(reader, token);
        }

        await using var native = new MySqlCommand(
            "SELECT DAYOFYEAR('2024-01-01'), DAYOFYEAR('2024-02-29'), " +
            "DAYOFYEAR('2024-12-31'), DAYOFYEAR(NULL)", db);
        await using var reference = await native.ExecuteReaderAsync(token);
        await AssertDayOfYear(reference, token);
    }

    private static async Task AssertDayOfYear(
        System.Data.Common.DbDataReader reader, CancellationToken token)
    {
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(new[] { 1, 60, 366 },
            Enumerable.Range(0, 3).Select(i => Convert.ToInt32(reader.GetValue(i))).ToArray());
        Assert.True(reader.IsDBNull(3));
        Assert.False(await reader.ReadAsync(token));
    }

    private static HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand Compile(
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(SourceSql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("portable-day-of-year-live"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
}
