using System.Globalization;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Real Oracle 26ai native-versus-typed APPLY with null-preserving outer cardinality.</summary>
public sealed class SqlCoreOracleApplyLiveParityTests
{
    [Theory]
    [InlineData("CROSS APPLY", false)]
    [InlineData("OUTER APPLY", true)]
    public async Task ApplyProducesIdenticalPhysicalRows(string kind, bool outer)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Oracle APPLY parity needs TEST_ORACLE_CONNECTION.");
            Assert.Skip("Oracle APPLY live parity requires TEST_ORACLE_CONNECTION.");
        }
        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var users = "HAPL_" + suffix + "_U";
        var orders = "HAPL_" + suffix + "_O";
        await using var db = new OracleConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (OracleException) when (retry < 60 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        var createdUsers = false;
        var createdOrders = false;
        try
        {
            await Execute(db, "CREATE TABLE " + users + " (ID NUMBER(10) PRIMARY KEY)", token);
            createdUsers = true;
            await Execute(db, "CREATE TABLE " + orders + " (USER_ID NUMBER(10), AMOUNT NUMBER(10))", token);
            createdOrders = true;
            foreach (var id in new[] { 1, 2, 3 })
                await Execute(db, $"INSERT INTO {users} VALUES ({id})", token);
            foreach (var (user, amount) in new[] { (1, 8), (1, 3), (2, 5) })
                await Execute(db, $"INSERT INTO {orders} VALUES ({user}, {amount})", token);
            var sql = "SELECT u.ID, q.AMOUNT FROM " + users + " u " + kind +
                " (SELECT o.AMOUNT AS AMOUNT FROM " + orders +
                " o WHERE o.USER_ID = u.ID AND o.AMOUNT >= 5) q ORDER BY u.ID";
            var native = await Snapshot(db, sql, [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle, new Version(26, 0));
            var command = SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.Oracle,
                SqlAgentToolType.Oracle, new SqlPlanValidationContext("oracle-apply-real"),
                new SqlExecutionPlanPolicy(0), profile, profile);
            Assert.Contains(kind, command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(command.Parameters, p => Convert.ToInt64(p.Value, CultureInfo.InvariantCulture) == 5);
            var rendered = await Snapshot(db, command.Sql, command.Parameters, token);
            Assert.Equal(outer ? new[] { "1:8", "2:5", "3:NULL" } : new[] { "1:8", "2:5" }, native);
            Assert.Equal(native, rendered);
        }
        finally
        {
            if (createdOrders) await Execute(db, "DROP TABLE " + orders + " PURGE", token);
            if (createdUsers) await Execute(db, "DROP TABLE " + users + " PURGE", token);
        }
    }

    private static async Task Execute(OracleConnection db, string sql, CancellationToken token)
    {
        using var command = new OracleCommand(sql, db);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string[]> Snapshot(OracleConnection db, string sql,
        IEnumerable<SqlParameterValue> bindings, CancellationToken token)
    {
        using var command = new OracleCommand(sql, db) { BindByName = true };
        foreach (var p in bindings)
            command.Parameters.Add(new OracleParameter(p.Name.TrimStart(':'), p.Value ?? DBNull.Value));
        using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<string>();
        while (await reader.ReadAsync(token))
            result.Add(reader.GetDecimal(0).ToString(CultureInfo.InvariantCulture) + ":" +
                (reader.IsDBNull(1) ? "NULL" : reader.GetDecimal(1).ToString(CultureInfo.InvariantCulture)));
        return result.ToArray();
    }
}
