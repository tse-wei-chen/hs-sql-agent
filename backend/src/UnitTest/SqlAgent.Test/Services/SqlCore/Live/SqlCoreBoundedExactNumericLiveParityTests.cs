using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Live provider execution proves that a string-valued parameter plus
/// an explicit, precisely derived DECIMAL(p,s) survives driver binding
/// and server conversion with no floating-point / default-scale loss.
/// The compiled command is executed as a derived table; the outer cast
/// to string prevents provider-side CLR decimal deserialization/overflow.
/// </summary>
public sealed class SqlCoreBoundedExactNumericLiveParityTests
{
    public static IEnumerable<object[]> LiveCases()
    {
        yield return [SqlAgentToolType.MySQL,
            "1234567890123456789012345678901234567890",
            "1234567890123456789012345678901234567890"];
        yield return [SqlAgentToolType.MySQL,
            new string('9', 65), new string('9', 65)];
        yield return [SqlAgentToolType.MySQL,
            "1e-29", "0." + new string('0', 28) + "1"];
        yield return [SqlAgentToolType.MySQL,
            "-1.00000000000000000000000000001",
            "-1.00000000000000000000000000001"];
        yield return [SqlAgentToolType.MsSqlServer,
            new string('9', 38), new string('9', 38)];
        yield return [SqlAgentToolType.MsSqlServer,
            "1e-29", "0." + new string('0', 28) + "1"];
        yield return [SqlAgentToolType.MsSqlServer,
            "-0.12345678901234567890123456789",
            "-0.12345678901234567890123456789"];
    }

    private static CompiledSqlCommand Compile(string value, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery("SELECT " + value + " AS amount",
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("bounded-exact-live-parity-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(LiveCases))]
    public async Task ActualDriverAndDatabase_PreserveExactBoundNumericValue(
        SqlAgentToolType target, string source, string expected)
    {
        // Both providers run against a real DB, but in separate CI shards.
        // The designated provider is mandatory within its own CI job.
        var assigned = Environment.GetEnvironmentVariable("SQLCORE_EXACT_NUMERIC_LIVE_PROVIDER");
        if (!string.IsNullOrWhiteSpace(assigned)
            && !string.Equals(assigned, target.ToString(), StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Exact numeric live parity for " + target + " runs in its dedicated CI shard.");
        var env = target == SqlAgentToolType.MySQL
            ? "TEST_MYSQL_CONNECTION" : "TEST_SQLSERVER_CONNECTION";
        var connection = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException(env + " required for numeric live parity.");
            Assert.Skip(env + " is required for live SQL numeric parity.");
        }

        var token = TestContext.Current.CancellationToken;
        var compiled = Compile(source, target);
        var binding = Assert.Single(compiled.Parameters);
        Assert.IsType<string>(binding.Value);
        // The inner command is the *actual compiled SQL*; the outer SQL cast
        // only controls how the database value is materialized into the test.
        var query = target == SqlAgentToolType.MySQL
            ? "SELECT CAST(n.amount AS CHAR) FROM (" + compiled.Sql + ") n"
            : "SELECT CONVERT(VARCHAR(100), n.amount) FROM (" + compiled.Sql + ") n";

        string actual;
        if (target == SqlAgentToolType.MySQL)
        {
            await using var db = new MySqlConnection(connection);
            await db.OpenAsync(token);
            await using var cmd = new MySqlCommand(query, db);
            cmd.Parameters.AddWithValue(binding.Name, binding.Value);
            actual = Assert.IsType<string>(await cmd.ExecuteScalarAsync(token));
        }
        else
        {
            await using var db = new SqlConnection(connection);
            await db.OpenAsync(token);
            await using var cmd = new SqlCommand(query, db);
            cmd.Parameters.AddWithValue(binding.Name, binding.Value);
            actual = Assert.IsType<string>(await cmd.ExecuteScalarAsync(token));
        }
        Assert.Equal(expected, actual);
    }
}
