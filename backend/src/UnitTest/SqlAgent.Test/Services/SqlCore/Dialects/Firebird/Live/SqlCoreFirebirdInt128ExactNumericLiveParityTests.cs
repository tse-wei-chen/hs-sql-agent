using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Independent Firebird 5 database and ADO.NET driver assurance for the
/// BigInteger -> INT128 -> NUMERIC(precision,0) execution path.
/// This is an actual database readback, not only SQL string verification.
/// </summary>
public sealed class SqlCoreFirebirdInt128ExactNumericLiveParityTests
{
    public static IEnumerable<object[]> ExactCases()
    {
        yield return [new string('9', 38), new string('9', 38), 38, 0];
        yield return ["12345678901234567890123456789012345678",
            "12345678901234567890123456789012345678", 38, 0];
        yield return ["1e+30", "1" + new string('0', 30), 31, 0];
        yield return ["-1e+30", "-1" + new string('0', 30), 31, 0];
    }

    [Theory]
    [MemberData(nameof(ExactCases))]
    public async Task ActualFirebird5DriverAndServerPreserveEveryExactDigit(
        string literal, string expected, int precision, int scale)
    {
        var connection = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Firebird exact INT128 parity requires TEST_FIREBIRD_CONNECTION in its dedicated CI job.");
            Assert.Skip("Firebird 5 live INT128 parity requires TEST_FIREBIRD_CONNECTION.");
        }

        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Firebird, new Version(5, 0));
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT " + literal + " AS amount FROM RDB$DATABASE",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-int128-live-v1"),
            new SqlExecutionPlanPolicy(), profile);
        Assert.Contains($"NUMERIC({precision},{scale})", compiled.Sql,
            StringComparison.OrdinalIgnoreCase);
        var binding = Assert.Single(compiled.Parameters);
        Assert.Equal(expected,
            Assert.IsType<System.Numerics.BigInteger>(binding.Value).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("AS INT128)", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DECFLOAT", compiled.Sql, StringComparison.OrdinalIgnoreCase);

        var token = TestContext.Current.CancellationToken;
        await using var db = new FbConnection(connection);
        await db.OpenAsync(token);
        // Read back as SQL VARCHAR, not CLR decimal, which cannot hold
        // a Firebird INT128-backed 38-digit fixed-point value.
        var sql = "SELECT CAST(e.amount AS VARCHAR(100)) FROM ("
            + compiled.Sql + ") e";
        await using var command = new FbCommand(sql, db);
        command.Parameters.AddWithValue(binding.Name, binding.Value);
        var result = await command.ExecuteScalarAsync(token);
        Assert.Equal(expected, Assert.IsType<string>(result).Trim());
    }
}
