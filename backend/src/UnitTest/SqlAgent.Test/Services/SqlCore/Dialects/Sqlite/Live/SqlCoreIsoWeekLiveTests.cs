using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>ISO week readbacks on existing provider CI infrastructure.
/// ISO weeks are checked against the independent CLR ISOWeek oracle.</summary>
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

    internal static async Task AssertWeeks(DbDataReader reader, CancellationToken token)
    {
        Assert.True(await reader.ReadAsync(token));
        for (var i = 0; i < Boundaries.Length; i++)
        {
            var actual = reader.GetValue(i);
            Assert.IsNotType<string>(actual);
            Assert.Equal(System.Globalization.ISOWeek.GetWeekOfYear(Boundaries[i]),
                Convert.ToInt32(actual));
        }
        Assert.True(reader.IsDBNull(Boundaries.Length));
        Assert.False(await reader.ReadAsync(token));
    }

    internal static HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand Compile(
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(Sql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("iso-week-live"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
}

public sealed class SqlCoreSqlServerIsoWeekLiveTests
{
    [Fact]
    public async Task SqlServer2022_IsoWeekMatchesCalendarBoundaryOracleAndNull()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_SQLSERVER_CONNECTION required.");
            Assert.Skip("Requires live SQL Server");
            return;
        }
        var token = TestContext.Current.CancellationToken;
        await using var db = new SqlConnection(new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await db.OpenAsync(token);
        var compiled = SqlCoreIsoWeekLiveTests.Compile(SqlAgentToolType.MsSqlServer);
        await using var command = db.CreateCommand();
        // Sunday-first is deliberately not the ISO Monday-first setting.
        command.CommandText = "SET DATEFIRST 7";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = compiled.Sql;
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        await SqlCoreIsoWeekLiveTests.AssertWeeks(reader, token);
    }
}

public sealed class SqlCoreOracleIsoWeekLiveTests
{
    [Fact]
    public async Task Oracle26_IsoWeekMatchesCalendarBoundaryOracleAndNull()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_ORACLE_CONNECTION required.");
            Assert.Skip("Requires live Oracle");
            return;
        }
        var token = TestContext.Current.CancellationToken;
        await using var db = new OracleConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (OracleException) when (retry < 60 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        var compiled = SqlCoreIsoWeekLiveTests.Compile(SqlAgentToolType.Oracle);
        await using var command = db.CreateCommand();
        command.BindByName = true;
        command.CommandText = compiled.Sql;
        foreach (var binding in compiled.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = binding.Name;
            parameter.Value = binding.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(token);
        await SqlCoreIsoWeekLiveTests.AssertWeeks(reader, token);
    }
}
