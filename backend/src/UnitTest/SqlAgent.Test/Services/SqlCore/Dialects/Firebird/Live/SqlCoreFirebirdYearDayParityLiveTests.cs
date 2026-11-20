using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A single physical Firebird 5 differential: Jan 1, leap day, and Dec 31.
/// The canonical PostgreSQL DOY value must be exactly native YEARDAY + 1.
/// Runs in the existing Firebird CI service, not ordinary test shards.
/// </summary>
public sealed class SqlCoreFirebirdYearDayParityLiveTests
{
    [Fact]
    public async Task FirebirdLive_PostgresDoyMatchesNativeYearDayPlusOneOnLeapYearBoundaries()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Firebird YEARDAY parity requires TEST_FIREBIRD_CONNECTION.");
            Assert.Skip("Firebird YEARDAY parity requires TEST_FIREBIRD_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var connection = new FbConnection(connectionString);
        await connection.OpenAsync(token);

        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT EXTRACT(DOY FROM DATE '2024-01-01') AS jan1, " +
            "EXTRACT(DOY FROM DATE '2024-02-29') AS leap_day, " +
            "EXTRACT(DOY FROM DATE '2024-12-31') AS dec31 FROM RDB$DATABASE",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-yearday-doy-live-v1"),
            new SqlExecutionPlanPolicy(), token);

        short[] canonical = new short[3];
        await using (var command = new FbCommand(compiled.Sql, connection))
        {
            foreach (var parameter in compiled.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            for (var i = 0; i < 3; i++)
                canonical[i] = reader.GetInt16(i);
            Assert.False(await reader.ReadAsync(token));
        }

        await using var native = new FbCommand(
            "SELECT EXTRACT(YEARDAY FROM DATE '2024-01-01'), " +
            "EXTRACT(YEARDAY FROM DATE '2024-02-29'), " +
            "EXTRACT(YEARDAY FROM DATE '2024-12-31') FROM RDB$DATABASE", connection);
        await using var nativeReader = await native.ExecuteReaderAsync(token);
        Assert.True(await nativeReader.ReadAsync(token));
        Assert.Equal(new short[] { 1, 60, 366 }, canonical);
        for (var i = 0; i < 3; i++)
            Assert.Equal(nativeReader.GetInt16(i) + 1, canonical[i]);
        Assert.False(await nativeReader.ReadAsync(token));
    }
}
