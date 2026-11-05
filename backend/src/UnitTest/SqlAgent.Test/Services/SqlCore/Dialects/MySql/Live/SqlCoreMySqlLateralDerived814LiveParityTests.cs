using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Compare native MySQL 8.0.14+ LATERAL results against parameterized F#
/// output on real relational data, including unmatched LEFT JOIN rows.
/// </summary>
public sealed class SqlCoreMySqlLateralDerived814LiveParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrelatedLateralQueryPreservesActualRows(bool leftJoin)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "TEST_MYSQL_CONNECTION is required for real MySQL LATERAL query parity.");
            Assert.Skip("Real MySQL 8.0.14+ LATERAL parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var db = new MySqlConnection(
            new MySqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await db.OpenAsync(token);
        await Execute(db, "CREATE TEMPORARY TABLE lateral_users(id INT PRIMARY KEY);" +
            "CREATE TEMPORARY TABLE lateral_orders(user_id INT, amount INT);" +
            "INSERT INTO lateral_users VALUES (1),(2),(3);" +
            "INSERT INTO lateral_orders VALUES (1,3),(1,8),(2,5),(2,1);", token);

        var join = leftJoin ? "LEFT JOIN LATERAL " : "INNER JOIN LATERAL ";
        const string projection = "SELECT u.id AS uid, picked.amount AS amount FROM lateral_users u ";
        const string derived = "(SELECT o.amount AS amount FROM lateral_orders o " +
            "WHERE o.user_id = u.id AND o.amount >= 2 ORDER BY o.amount DESC LIMIT 1) picked ON TRUE " +
            "ORDER BY u.id";
        var sql = projection + join + derived;

        var expected = await Snapshot(db, sql, [], token);
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL,
            new Version(8, 0, 36));
        var compiled = SqlCoreFacade.CompileQuery(sql,
            SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-lateral-live"),
            new SqlExecutionPlanPolicy(0), profile, profile);

        Assert.Equal(SqlStatementKind.Select, compiled.Kind);
        Assert.Contains(" JOIN LATERAL ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 1));
        var actual = await Snapshot(db, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(leftJoin ? new[] { "1:8", "2:5", "3:NULL" }
                              : new[] { "1:8", "2:5" }, expected);
        Assert.Equal(expected, actual);
    }

    private static async Task Execute(MySqlConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string[]> Snapshot(MySqlConnection connection, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(reader.GetInt32(0) + ":" +
                     (reader.IsDBNull(1) ? "NULL" : reader.GetInt32(1).ToString()));
        return rows.ToArray();
    }
}
