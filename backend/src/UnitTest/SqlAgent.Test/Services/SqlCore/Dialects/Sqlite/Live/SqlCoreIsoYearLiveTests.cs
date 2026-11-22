using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Compare one real MySQL 8.4 and one in-memory SQLite query against a fixed
/// ISO week-year oracle on cross-year boundaries. Reuse existing CI services.
/// </summary>
public sealed class SqlCoreIsoYearLiveTests
{
    private static readonly DateTime[] Dates =
    [
        new(2018, 12, 31), // ISO 2019, Gregorian 2018
        new(2021, 1, 1),   // ISO 2020, Gregorian 2021
        new(2021, 1, 4),   // ISO 2021, Gregorian 2021
        new(2020, 12, 31)  // ISO 2020, Gregorian 2020
    ];

    private const string SourceSql =
        "SELECT EXTRACT(ISOYEAR FROM DATE '2018-12-31') AS next_year, " +
        "EXTRACT(ISOYEAR FROM DATE '2021-01-01') AS prior_year, " +
        "EXTRACT(ISOYEAR FROM DATE '2021-01-04') AS normal_year, " +
        "EXTRACT(ISOYEAR FROM DATE '2020-12-31') AS same_year, " +
        "EXTRACT(ISOYEAR FROM CAST(NULL AS DATE)) AS null_year";

    [Fact]
    public async Task SqliteLive_IsoYearMatchesIndependentWeekYearOracleAndNull()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.Sqlite);

        await using (var command = db.CreateCommand())
        {
            command.CommandText = compiled.Sql;
            foreach (var parameter in compiled.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            await AssertIsoYear(reader, token);
        }

        await using var native = db.CreateCommand();
        native.CommandText =
            "SELECT STRFTIME('%Y', '2018-12-31'), STRFTIME('%Y', '2021-01-01')";
        await using var reference = await native.ExecuteReaderAsync(token);
        Assert.True(await reference.ReadAsync(token));
        Assert.Equal("2018", reference.GetString(0));
        Assert.Equal("2021", reference.GetString(1));
    }

    [Fact]
    public async Task MySql84Live_IsoYearMatchesIndependentWeekYearOracleAndNull()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "MySQL ISO year physical parity requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("MySQL ISO year parity requires TEST_MYSQL_CONNECTION.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var db = new MySqlConnection(
            new MySqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await db.OpenAsync(token);
        var compiled = Compile(SqlAgentToolType.MySQL);
        await using (var command = new MySqlCommand(compiled.Sql, db))
        {
            foreach (var parameter in compiled.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            await AssertIsoYear(reader, token);
        }

        await using var native = new MySqlCommand(
            "SELECT YEARWEEK('2018-12-31', 3), YEARWEEK('2021-01-01', 3)", db);
        await using var reference = await native.ExecuteReaderAsync(token);
        Assert.True(await reference.ReadAsync(token));
        Assert.Equal(201901, Convert.ToInt32(reference.GetValue(0)));
        Assert.Equal(202053, Convert.ToInt32(reference.GetValue(1)));
    }

    private static async Task AssertIsoYear(DbDataReader reader, CancellationToken token)
    {
        Assert.True(await reader.ReadAsync(token));
        for (var i = 0; i < Dates.Length; i++)
            Assert.Equal(System.Globalization.ISOWeek.GetYear(Dates[i]),
                Convert.ToInt32(reader.GetValue(i)));
        Assert.True(reader.IsDBNull(Dates.Length));
        Assert.False(await reader.ReadAsync(token));
    }

    private static HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand Compile(
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(SourceSql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("iso-year-live"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
}
