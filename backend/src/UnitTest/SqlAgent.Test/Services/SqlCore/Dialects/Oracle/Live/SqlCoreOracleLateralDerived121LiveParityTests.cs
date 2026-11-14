using System.Globalization;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real Oracle native-vs-compiled DQL LATERAL parity. Isolated physical
/// objects and explicit teardown are required because Oracle DDL commits.
/// </summary>
public sealed class SqlCoreOracleLateralDerived121LiveParityTests
{
    [Theory]
    [InlineData("CROSS JOIN", false)]
    [InlineData("INNER JOIN", false)]
    [InlineData("LEFT JOIN", true)]
    public async Task OracleLateralRowsMatchNativeAndPreserveUnmatchedOuterRows(
        string joinKind, bool leftJoin)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Real Oracle LATERAL parity requires TEST_ORACLE_CONNECTION in CI.");
            Assert.Skip("Real Oracle LATERAL parity requires TEST_ORACLE_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var users = "HLAT_" + suffix + "_U";
        var orders = "HLAT_" + suffix + "_O";

        await using var db = new OracleConnection(cs);
        for (var attempt = 0; ; attempt++)
        {
            try { await db.OpenAsync(token); break; }
            catch (OracleException) when (attempt < 60 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }

        var usersCreated = false;
        var ordersCreated = false;
        try
        {
            await Execute(db, "CREATE TABLE " + users + " (ID NUMBER(10) PRIMARY KEY)", token);
            usersCreated = true;
            await Execute(db, "CREATE TABLE " + orders +
                " (USER_ID NUMBER(10), AMOUNT NUMBER(10))", token);
            ordersCreated = true;
            await Execute(db, "INSERT INTO " + users + " VALUES (1)", token);
            await Execute(db, "INSERT INTO " + users + " VALUES (2)", token);
            await Execute(db, "INSERT INTO " + users + " VALUES (3)", token);
            await Execute(db, "INSERT INTO " + orders + " VALUES (1,8)", token);
            await Execute(db, "INSERT INTO " + orders + " VALUES (1,3)", token);
            await Execute(db, "INSERT INTO " + orders + " VALUES (2,5)", token);

            var sql = "SELECT u.ID, q.AMOUNT FROM " + users + " u " +
                joinKind + " LATERAL (SELECT o.AMOUNT AS AMOUNT FROM " + orders +
                " o WHERE o.USER_ID = u.ID AND o.AMOUNT >= 5) q" +
                (joinKind == "CROSS JOIN" ? "" : " ON 1 = 1") +
                " ORDER BY u.ID, q.AMOUNT";
            var expected = await Snapshot(db, sql, [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle,
                new Version(26, 0));
            var compiled = SqlCoreFacade.CompileQuery(sql,
                SqlAgentToolType.Oracle, SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("oracle-lateral-live-parity"),
                new SqlExecutionPlanPolicy(0), profile, profile);
            Assert.Equal(SqlStatementKind.Select, compiled.Kind);
            Assert.Contains(" JOIN LATERAL ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p => Convert.ToInt64(p.Value, CultureInfo.InvariantCulture) == 5L);
            var actual = await Snapshot(db, compiled.Sql, compiled.Parameters, token);
            Assert.Equal(leftJoin
                ? new[] { "1:8", "2:5", "3:NULL" }
                : new[] { "1:8", "2:5" }, expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (ordersCreated)
                await Execute(db, "DROP TABLE " + orders + " PURGE", token);
            if (usersCreated)
                await Execute(db, "DROP TABLE " + users + " PURGE", token);
        }
    }

    private static async Task Execute(OracleConnection db, string sql, CancellationToken ct)
    {
        using var command = new OracleCommand(sql, db);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string[]> Snapshot(OracleConnection db, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken ct)
    {
        using var command = new OracleCommand(sql, db) { BindByName = true };
        foreach (var binding in parameters)
            command.Parameters.Add(new OracleParameter(
                binding.Name.TrimStart(':'), binding.Value ?? DBNull.Value));
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetDecimal(0).ToString(CultureInfo.InvariantCulture);
            var amount = reader.IsDBNull(1)
                ? "NULL" : reader.GetDecimal(1).ToString(CultureInfo.InvariantCulture);
            rows.Add(id + ":" + amount);
        }
        return rows.ToArray();
    }
}
