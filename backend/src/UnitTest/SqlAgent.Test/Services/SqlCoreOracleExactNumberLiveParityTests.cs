using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Oracle 23ai live parity, executed only on the dedicated Oracle service
/// shard. It deliberately changes the session decimal separator to ','
/// before executing the ACTUAL compiled text-parameter query.
/// </summary>
public sealed class SqlCoreOracleExactNumberLiveParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [new string('9', 38), new string('9', 38), 38, 0];
        yield return ["12345678901234567890123456789012345678",
            "12345678901234567890123456789012345678", 38, 0];
        yield return ["1e-29", "0." + new string('0', 28) + "1", 29, 29];
        yield return ["-0.12345678901234567890123456789",
            "-0.12345678901234567890123456789", 29, 29];
        yield return ["1.00000000000000000000000000001",
            "1.00000000000000000000000000001", 30, 29];
        yield return ["1e+30", "1" + new string('0', 30), 31, 0];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Oracle23aiDriverBindingIsExactEvenWithCommaDecimalSession(
        string source, string expected, int precision, int scale)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SQLCORE_ORACLE_EXACT_NUMERIC_LIVE"),
                "true", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Oracle exact numeric live tests run in the dedicated Oracle CI shard.");

        var cs = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Oracle exact numeric parity requires TEST_ORACLE_CONNECTION.");

        var token = TestContext.Current.CancellationToken;
        await using var db = new OracleConnection(cs);
        for (var attempt = 0; ; attempt++)
        {
            try { await db.OpenAsync(token); break; }
            catch (OracleException) when (attempt < 60 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }

        using (var alter = new OracleCommand(
            "ALTER SESSION SET NLS_NUMERIC_CHARACTERS = ',.'", db))
            await alter.ExecuteNonQueryAsync(token);

        var compiled = SqlCoreFacade.CompileQuery("SELECT " + source + " AS amount",
            SqlAgentToolType.Postgres, SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("oracle-numeric-live-parity"),
            new SqlExecutionPlanPolicy(), token);
        Assert.Contains("NLS_NUMERIC_CHARACTERS", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        var binding = Assert.Single(compiled.Parameters);
        Assert.Equal(expected, Assert.IsType<string>(binding.Value));

        // Server-side string formatting prevents OracleDecimal -> CLR Decimal
        // overflow. Its format is derived only from the expected type shape.
        var integerPositions = Math.Max(1, precision - scale);
        var format = "FM" + new string('9', integerPositions - 1) + "0"
            + (scale == 0 ? "" : "D" + new string('0', scale));
        var sql = "SELECT TO_CHAR(q.amount, '" + format
            + "', 'NLS_NUMERIC_CHARACTERS=''.,''') FROM (" + compiled.Sql + ") q";
        using var command = new OracleCommand(sql, db) { BindByName = true };
        command.Parameters.Add(new OracleParameter(binding.Name.TrimStart(':'), binding.Value));
        var actual = await command.ExecuteScalarAsync(token);
        Assert.Equal(expected, Assert.IsType<string>(actual));
    }
}
