using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Physical Firebird 5 proof for time-of-day extraction, including decimal seconds.
/// Executed only in the existing pinned Firebird live CI job.
/// </summary>
public sealed class SqlCoreFirebirdClockExtractLiveTests
{
    [Fact]
    public async Task FirebirdLive_ClockPartsRetainTimeAndFractionalSeconds()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Firebird clock parity requires TEST_FIREBIRD_CONNECTION.");
            Assert.Skip("Firebird clock parity requires TEST_FIREBIRD_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var connection = new FbConnection(connectionString);
        await connection.OpenAsync(token);
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT EXTRACT(HOUR FROM TIMESTAMP '2021-09-21 14:05:06.2500') AS hour_no, " +
            "EXTRACT(MINUTE FROM TIMESTAMP '2021-09-21 14:05:06.2500') AS minute_no, " +
            "EXTRACT(SECOND FROM TIMESTAMP '2021-09-21 14:05:06.2500') AS second_no FROM RDB$DATABASE",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-clock-live-v1"),
            new SqlExecutionPlanPolicy(), token);

        await using var command = new FbCommand(compiled.Sql, connection);
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(14, Convert.ToInt32(reader.GetValue(0)));
        Assert.Equal(5, Convert.ToInt32(reader.GetValue(1)));
        Assert.Equal(6.25m, Convert.ToDecimal(reader.GetValue(2)));
        Assert.False(await reader.ReadAsync(token));
    }
}
