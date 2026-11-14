using System.Globalization;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>SQL Server 2022 native-versus-typed APPLY on physical tables.</summary>
public sealed class SqlCoreSqlServerApplyLiveParityTests
{
    [Theory]
    [InlineData("CROSS APPLY", false)]
    [InlineData("OUTER APPLY", true)]
    public async Task ApplyPreservesRowsAndNulls(string kind, bool outer)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server APPLY parity needs TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server APPLY live parity requires TEST_SQLSERVER_CONNECTION.");
        }
        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var users = "happly_" + suffix + "_u";
        var orders = "happly_" + suffix + "_o";
        await using var db = new SqlConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        var usersCreated = false;
        var ordersCreated = false;
        try
        {
            await Execute(db, "CREATE TABLE " + users + " (ID INT PRIMARY KEY)", token);
            usersCreated = true;
            await Execute(db, "CREATE TABLE " + orders + " (USER_ID INT, AMOUNT INT)", token);
            ordersCreated = true;
            await Execute(db, "INSERT INTO " + users + " VALUES(1),(2),(3)", token);
            await Execute(db, "INSERT INTO " + orders + " VALUES(1,8),(1,3),(2,5)", token);
            var sql = "SELECT u.ID, q.AMOUNT FROM " + users + " u " + kind +
                " (SELECT o.AMOUNT AS AMOUNT FROM " + orders +
                " o WHERE o.USER_ID = u.ID AND o.AMOUNT >= 5) q ORDER BY u.ID";
            var native = await Snapshot(db, sql, [], token);
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var command = SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.MsSqlServer,
                SqlAgentToolType.MsSqlServer, new SqlPlanValidationContext("sqlserver-apply-real"),
                new SqlExecutionPlanPolicy(0), profile, profile);
            Assert.Contains(kind, command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(command.Parameters, p => Convert.ToInt64(p.Value, CultureInfo.InvariantCulture) == 5);
            var rendered = await Snapshot(db, command.Sql, command.Parameters, token);
            Assert.Equal(outer ? new[] { "1:8", "2:5", "3:NULL" } : new[] { "1:8", "2:5" }, native);
            Assert.Equal(native, rendered);
        }
        finally
        {
            if (ordersCreated) await Execute(db, "DROP TABLE " + orders, token);
            if (usersCreated) await Execute(db, "DROP TABLE " + users, token);
        }
    }

    private static async Task Execute(SqlConnection db, string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string[]> Snapshot(SqlConnection db, string sql,
        IEnumerable<SqlParameterValue> bindings, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, db);
        foreach (var p in bindings)
            command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<string>();
        while (await reader.ReadAsync(token))
            result.Add(reader.GetInt32(0).ToString(CultureInfo.InvariantCulture) + ":" +
                (reader.IsDBNull(1) ? "NULL" : reader.GetInt32(1).ToString(CultureInfo.InvariantCulture)));
        return result.ToArray();
    }
}
