using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// One actual MySQL 8.4 readback, including Sunday/Monday/Saturday and NULL.
/// Reuses the existing MySQL CI service, not a new test container.
/// </summary>
public sealed class SqlCoreMySqlIsoWeekdayLiveTests
{
    [Fact]
    public async Task MySqlLive_CompiledIsoDowMatchesNativeWeekdayOnBoundaries()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "MySQL ISO weekday parity requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("MySQL live parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var db = new MySqlConnection(
            new MySqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await db.OpenAsync(token);

        await using (var setup = new MySqlCommand(
            "CREATE TEMPORARY TABLE iso_weekday_cases(id INT PRIMARY KEY, event_date DATE NULL);" +
            "INSERT INTO iso_weekday_cases VALUES " +
            "(1, '2021-01-03'), (2, '2021-01-04'), (3, '2021-01-09'), (4, NULL);", db))
            await setup.ExecuteNonQueryAsync(token);

        const string sourceSql =
            "SELECT EXTRACT(ISODOW FROM event_date) AS iso_day " +
            "FROM iso_weekday_cases ORDER BY id";
        var compiled = SqlCoreFacade.CompileQuery(
            sourceSql, SqlAgentToolType.Postgres, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-isodow-live"),
            new SqlExecutionPlanPolicy(), token);
        Assert.Contains("WEEKDAY(", compiled.Sql, StringComparison.OrdinalIgnoreCase);

        var native = await Read(db,
            "SELECT WEEKDAY(event_date) + 1 AS iso_day " +
            "FROM iso_weekday_cases ORDER BY id", [], token);
        var actual = await Read(db, compiled.Sql, compiled.Parameters, token);

        Assert.Equal(new string[] { "7", "1", "6", "NULL" }, native);
        Assert.Equal(native, actual);
    }

    private static async Task<string[]> Read(
        MySqlConnection db, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var command = new MySqlCommand(sql, db);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        var results = new List<string>();
        while (await reader.ReadAsync(token))
            results.Add(reader.IsDBNull(0) ? "NULL" : Convert.ToInt32(reader.GetValue(0)).ToString());
        return results.ToArray();
    }
}
