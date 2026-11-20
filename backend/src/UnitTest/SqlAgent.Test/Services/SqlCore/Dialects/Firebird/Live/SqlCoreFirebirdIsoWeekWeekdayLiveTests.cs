using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// One physical Firebird 5 readback: ISO week-year transition,
/// native Sunday=0, and portable ISO weekdays Monday=1..Sunday=7.
/// Runs only in the existing dedicated Firebird CI service, not the unit shard.
/// </summary>
public sealed class SqlCoreFirebirdIsoWeekWeekdayLiveTests
{
    [Fact]
    public async Task FirebirdLive_WeekYearBoundaryAndSundayMatchNativeValues()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Firebird date-part parity requires TEST_FIREBIRD_CONNECTION.");
            Assert.Skip("Firebird live parity requires TEST_FIREBIRD_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var connection = new FbConnection(connectionString);
        await connection.OpenAsync(token);
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT EXTRACT(WEEK FROM DATE '2021-01-01') AS week_no, " +
            "EXTRACT(DOW FROM DATE '2021-01-03') AS weekday_no, " +
            "EXTRACT(ISODOW FROM DATE '2021-01-03') AS iso_sunday, " +
            "EXTRACT(ISODOW FROM DATE '2021-01-04') AS iso_monday, " +
            "EXTRACT(ISODOW FROM DATE '2021-01-09') AS iso_saturday FROM RDB$DATABASE",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-date-part-live-v1"),
            new SqlExecutionPlanPolicy(), token);

        await using var command = new FbCommand(compiled.Sql, connection);
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(53, Convert.ToInt32(reader.GetValue(0)));
        Assert.Equal(0, Convert.ToInt32(reader.GetValue(1)));
        // Firebird's native WEEKDAY is Sunday=0; the PostgreSQL ISODOW
        // lowering must preserve both weekday numbering and SMALLINT metadata.
        Assert.Equal((short)7, reader.GetInt16(2));
        Assert.Equal((short)1, reader.GetInt16(3));
        Assert.Equal((short)6, reader.GetInt16(4));
        Assert.False(await reader.ReadAsync(token));
    }
}
