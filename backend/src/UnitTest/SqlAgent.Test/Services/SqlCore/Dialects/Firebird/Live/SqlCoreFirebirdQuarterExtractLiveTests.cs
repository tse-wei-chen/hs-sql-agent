using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A single Firebird 5 live differential check against native quarter.
/// Only the compiled path uses the portable, older-version-safe MONTH algebra.
/// </summary>
public sealed class SqlCoreFirebirdQuarterExtractLiveTests
{
    [Fact]
    public async Task FirebirdLive_PortableQuarterMatchesNativeAtYearBoundaries()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Firebird QUARTER parity requires TEST_FIREBIRD_CONNECTION.");
            Assert.Skip("Firebird QUARTER parity requires TEST_FIREBIRD_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var connection = new FbConnection(connectionString);
        await connection.OpenAsync(token);

        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT EXTRACT(QUARTER FROM DATE '2021-01-01') AS q1, " +
            "EXTRACT(QUARTER FROM DATE '2021-12-31') AS q4 FROM RDB$DATABASE",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-quarter-live-v1"),
            new SqlExecutionPlanPolicy(), token);

        int q1;
        int q4;
        await using (var command = new FbCommand(compiled.Sql, connection))
        {
            foreach (var parameter in compiled.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            q1 = Convert.ToInt32(reader.GetValue(0));
            q4 = Convert.ToInt32(reader.GetValue(1));
            Assert.False(await reader.ReadAsync(token));
        }

        await using var native = new FbCommand(
            "SELECT EXTRACT(QUARTER FROM DATE '2021-01-01'), " +
            "EXTRACT(QUARTER FROM DATE '2021-12-31') FROM RDB$DATABASE", connection);
        await using var nativeReader = await native.ExecuteReaderAsync(token);
        Assert.True(await nativeReader.ReadAsync(token));
        Assert.Equal(1, q1);
        Assert.Equal(4, q4);
        Assert.Equal(Convert.ToInt32(nativeReader.GetValue(0)), q1);
        Assert.Equal(Convert.ToInt32(nativeReader.GetValue(1)), q4);
    }
}
