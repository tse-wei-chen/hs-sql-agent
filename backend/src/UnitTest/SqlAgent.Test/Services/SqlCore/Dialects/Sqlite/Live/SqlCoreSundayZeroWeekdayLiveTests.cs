using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// One independent native-versus-compiled readback on each existing MySQL 8.4
/// service / in-memory SQLite target; never starts a per-case test container.
/// </summary>
public sealed class SqlCoreSundayZeroWeekdayLiveTests
{
    private const string SourceSql =
        "SELECT EXTRACT(DOW FROM DATE '2021-01-03') AS sunday, " +
        "EXTRACT(DOW FROM DATE '2021-01-04') AS monday, " +
        "EXTRACT(DOW FROM DATE '2021-01-09') AS saturday, " +
        "EXTRACT(DOW FROM CAST(NULL AS DATE)) AS null_day";

    [Fact]
    public async Task SqliteLive_SundayZeroWeekdayMatchesNativeAndNull()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.Sqlite);

        await using (var command = db.CreateCommand())
        {
            command.CommandText = compiled.Sql;
            foreach (var parameter in compiled.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using (var reader = await command.ExecuteReaderAsync(token))
                await AssertSundayZero(reader, token);
        }

        await using var native = db.CreateCommand();
        native.CommandText =
            "SELECT STRFTIME('%w','2021-01-03'), " +
            "STRFTIME('%w','2021-01-04'), STRFTIME('%w','2021-01-09'), STRFTIME('%w',NULL)";
        await using var reference = await native.ExecuteReaderAsync(token);
        Assert.True(await reference.ReadAsync(token));
        Assert.Equal(new[] { "0", "1", "6" }, Enumerable.Range(0, 3)
            .Select(reference.GetString).ToArray());
        Assert.True(reference.IsDBNull(3));
        Assert.False(await reference.ReadAsync(token));
    }

    [Fact]
    public async Task MySql84Live_SundayZeroWeekdayMatchesNativeAndNull()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "MySQL Sunday-zero weekday parity requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("MySQL Sunday-zero weekday parity requires TEST_MYSQL_CONNECTION.");
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
                await AssertSundayZero(reader, token);
        }

        await using var native = new MySqlCommand(
            "SELECT DAYOFWEEK('2021-01-03'), DAYOFWEEK('2021-01-04'), " +
            "DAYOFWEEK('2021-01-09'), DAYOFWEEK(NULL)", db);
        await using var reference = await native.ExecuteReaderAsync(token);
        Assert.True(await reference.ReadAsync(token));
        Assert.Equal(new[] { 1, 2, 7 }, Enumerable.Range(0, 3)
            .Select(i => Convert.ToInt32(reference.GetValue(i))).ToArray());
        Assert.True(reference.IsDBNull(3));
        Assert.False(await reference.ReadAsync(token));
    }

    private static async Task AssertSundayZero(DbDataReader reader, CancellationToken token)
    {
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(new[] { 0, 1, 6 }, Enumerable.Range(0, 3)
            .Select(i => Convert.ToInt32(reader.GetValue(i))).ToArray());
        Assert.True(reader.IsDBNull(3));
        Assert.False(await reader.ReadAsync(token));
    }

    private static HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand Compile(
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(SourceSql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("sunday-zero-weekday-live"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
}
