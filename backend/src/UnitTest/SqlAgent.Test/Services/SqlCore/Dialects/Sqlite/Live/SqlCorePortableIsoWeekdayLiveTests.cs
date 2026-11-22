using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// One physical readback per target, using the existing MySQL 8.4 CI service
/// and in-memory SQLite. Neither requires a separate container lifecycle.
/// </summary>
public sealed class SqlCorePortableIsoWeekdayLiveTests
{
    private const string SourceSql =
        "SELECT EXTRACT(ISODOW FROM DATE '2021-01-03') AS sunday, " +
        "EXTRACT(ISODOW FROM DATE '2021-01-04') AS monday, " +
        "EXTRACT(ISODOW FROM DATE '2021-01-09') AS saturday";

    [Fact]
    public async Task SqliteLive_IsoDowMaintainsMondayOneSundaySeven()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.Sqlite);

        await using var command = db.CreateCommand();
        command.CommandText = compiled.Sql;
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(new[] { 7, 1, 6 }, Enumerable.Range(0, 3)
                .Select(i => Convert.ToInt32(reader.GetValue(i))).ToArray());
            Assert.False(await reader.ReadAsync(token));
        }

        // Independent native SQLite source numbering uses Sunday=0.
        await using var native = db.CreateCommand();
        native.CommandText = "SELECT STRFTIME('%w','2021-01-03'), " +
            "STRFTIME('%w','2021-01-04'), STRFTIME('%w','2021-01-09')";
        await using var reference = await native.ExecuteReaderAsync(token);
        Assert.True(await reference.ReadAsync(token));
        Assert.Equal(new[] { "0", "1", "6" }, Enumerable.Range(0, 3)
            .Select(i => reference.GetString(i)).ToArray());
    }

    [Fact]
    public async Task MySql84Live_IsoDowMatchesNativeWeekdayAtBoundaries()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "MySQL ISO weekday physical parity requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("MySQL ISO weekday parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var db = new MySqlConnection(
            new MySqlConnectionStringBuilder(connectionString) { Pooling = false }
                .ConnectionString);
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.MySQL);

        await using var command = new MySqlCommand(compiled.Sql, db);
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(new[] { 7, 1, 6 }, Enumerable.Range(0, 3)
                .Select(i => Convert.ToInt32(reader.GetValue(i))).ToArray());
            Assert.False(await reader.ReadAsync(token));
        }

        await using var native = new MySqlCommand(
            "SELECT WEEKDAY('2021-01-03'), WEEKDAY('2021-01-04'), WEEKDAY('2021-01-09')", db);
        await using var reference = await native.ExecuteReaderAsync(token);
        Assert.True(await reference.ReadAsync(token));
        Assert.Equal(new[] { 6, 0, 5 }, Enumerable.Range(0, 3)
            .Select(i => Convert.ToInt32(reference.GetValue(i))).ToArray());
    }

    private static HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand Compile(
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(SourceSql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("iso-weekday-live"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
}
