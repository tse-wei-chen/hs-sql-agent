using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>One native MySQL 8.4 and one SQLite in-memory readback on existing
/// CI infrastructure. ISO weeks are checked against the CLR ISOWeek oracle.</summary>
public sealed class SqlCoreIsoWeekLiveTests
{
    private static readonly DateTime[] Boundaries =
    [
        new(2018, 12, 31), // ISO week 1 of 2019, not calendar week 53
        new(2021, 1, 1),   // ISO week 53 of 2020
        new(2021, 1, 4)    // ISO week 1 of 2021
    ];

    private const string Sql =
        "SELECT EXTRACT(WEEK FROM DATE '2018-12-31') AS next_year, " +
        "EXTRACT(WEEK FROM DATE '2021-01-01') AS previous_year, " +
        "EXTRACT(WEEK FROM DATE '2021-01-04') AS new_week, " +
        "EXTRACT(WEEK FROM CAST(NULL AS DATE)) AS null_week";

    [Fact]
    public async Task SqliteLive_IsoWeekMatchesCalendarBoundaryOracleAndNull()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.Sqlite);
        await using var command = db.CreateCommand();
        command.CommandText = compiled.Sql;
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        await AssertWeeks(reader, token);
    }

    [Fact]
    public async Task MySql84Live_IsoWeekMatchesCalendarBoundaryOracleAndNull()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Real MySQL ISO week parity requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("Real MySQL ISO week parity requires TEST_MYSQL_CONNECTION.");
        }
        var token = TestContext.Current.CancellationToken;
        await using var db = new MySqlConnection(
            new MySqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.MySQL);
        await using var command = new MySqlCommand(compiled.Sql, db);
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        await AssertWeeks(reader, token);
    }

    private static async Task AssertWeeks(DbDataReader reader, CancellationToken token)
    {
        Assert.True(await reader.ReadAsync(token));
        for (var i = 0; i < Boundaries.Length; i++)
            Assert.Equal(System.Globalization.ISOWeek.GetWeekOfYear(Boundaries[i]),
                Convert.ToInt32(reader.GetValue(i)));
        Assert.True(reader.IsDBNull(Boundaries.Length));
        Assert.False(await reader.ReadAsync(token));
    }

    private static HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand Compile(
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(Sql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("iso-week-live"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
}
