using System.Data.Common;
using HsSqlAgent.SqlCore;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G10/Q02: bounded COUNT FILTER semantics, including false predicates,
/// NULL count operands and empty window frames. SQL Server/MySQL lower
/// exclusively to COUNT(CASE WHEN ...), not SUM(CASE...) which returns NULL
/// on empty row sets.
/// </summary>
public sealed class SqlCoreFilteredCountLoweringTests
{
    private const string Source = "orders";

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(*)", false)]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(amount)", false)]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(*)", false)]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(amount)", false)]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(*)", true)]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(amount)", true)]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(*)", true)]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(amount)", true)]
    public void CountFilter_CompilesToSemanticallyEquivalentCaseCount(
        SqlAgentToolType target, string aggregate, bool window)
    {
        var sql = Query(aggregate, window, "status = 1");
        var compiled = Compile(sql, target);
        var expectedAggregate = target == SqlAgentToolType.MsSqlServer
            ? "COUNT_BIG(CASE WHEN" : "COUNT(CASE WHEN";
        Assert.Contains(expectedAggregate, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THEN", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("END)", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FILTER (WHERE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUM(CASE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        if (window)
            Assert.Contains("OVER (PARTITION BY", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void FilteredCountCapability_HasDedicatedBoundedTranslation(SqlAgentToolType provider)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);
        Assert.Equal(SqlCapabilityStatus.Rejected,
            Assert.Single(matrix.Capabilities, item => item.Id == "expression.filter").Status);
        Assert.Equal(SqlCapabilityStatus.Translated,
            Assert.Single(matrix.Capabilities, item => item.Id == "aggregate.filtered_count").Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "SUM(amount)")]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(DISTINCT amount)")]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(amount + 1)")]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(1)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SUM(amount)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(DISTINCT amount)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(amount + 1)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(1)")]
    public void UnprovenFilteredAggregateShapes_StillFailClosed(
        SqlAgentToolType target, string aggregate)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(Query(aggregate, false, "status = 1"), target));
        Assert.Contains("expression.filter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void RawSourceFilterSyntax_RemainsRejected(SqlAgentToolType source)
    {
        Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                Query("COUNT(*)", false, "status = 1"),
                source, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("g10-filter-count-source"),
                new SqlExecutionPlanPolicy()));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public async Task RealDatabase_NullAndEmptyCasesAgreeWithNativeFilteredCount(
        SqlAgentToolType provider)
    {
        var cs = Environment.GetEnvironmentVariable(provider == SqlAgentToolType.Postgres
            ? "TEST_POSTGRES_CONNECTION" : "TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native FILTER parity requires real " + provider + " connection in CI.");
            Assert.Skip("Real database connection required for G10 FILTER parity.");
            return;
        }

        await using DbConnection connection = provider == SqlAgentToolType.Postgres
            ? new NpgsqlConnection(cs) : new MySqlConnection(cs);
        var token = TestContext.Current.CancellationToken;
        await connection.OpenAsync(token);
        var table = "sqlcore_filtered_count_" + Guid.NewGuid().ToString("N");
        var create = provider == SqlAgentToolType.Postgres
            ? "CREATE TEMP TABLE " + table + " (id INTEGER, grp INTEGER, amount INTEGER NULL, status INTEGER)"
            : "CREATE TEMPORARY TABLE " + table + " (id INTEGER, grp INTEGER, amount INTEGER NULL, status INTEGER)";
        await Execute(connection, create, [], token);
        await Execute(connection,
            $"INSERT INTO {table} (id,grp,amount,status) VALUES (1,1,10,1),(2,1,NULL,1),(3,2,20,0),(4,2,NULL,1)", [], token);

        foreach (var (agg, predicate, window, expected) in Cases())
        {
            var sql = Query(agg, window, predicate, table);
            var compiled = Compile(sql, provider);
            var actual = await ReadCounts(connection, compiled.Sql, compiled.Parameters, window, token);
            Assert.Equal(expected, actual);
        }
    }

    internal static IEnumerable<(string Aggregate, string Predicate, bool Window, long[] Expected)> Cases()
    {
        yield return ("COUNT(*)", "status = 1", false, [3]);
        yield return ("COUNT(amount)", "status = 1", false, [1]);
        yield return ("COUNT(*)", "status = 99", false, [0]);
        yield return ("COUNT(amount)", "status = 99", false, [0]);
        yield return ("COUNT(*)", "status = 1", true, [1, 2, 0, 1]);
        yield return ("COUNT(amount)", "status = 1", true, [1, 1, 0, 0]);
    }

    internal static string Query(string aggregate, bool window, string predicate, string table = Source) =>
        "SELECT " + (window ? "id, " : "") + aggregate + " FILTER (WHERE " + predicate + ")" +
        (window ? " OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)" : "") +
        " FROM " + table + (window ? " ORDER BY id" : "");

    internal static CompiledSqlCommand Compile(string sql, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("g10-filter-count-v1"), new SqlExecutionPlanPolicy());

    internal static async Task Execute(DbConnection conn, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, parameters);
        await cmd.ExecuteNonQueryAsync(token);
    }

    internal static async Task<long[]> ReadCounts(
        DbConnection conn, string sql, IEnumerable<SqlParameterValue> parameters,
        bool window, CancellationToken token)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var values = new List<long>();
        while (await reader.ReadAsync(token))
            values.Add(Convert.ToInt64(reader.GetValue(window ? 1 : 0)));
        return values.ToArray();
    }

    private static void Bind(DbCommand cmd, IEnumerable<SqlParameterValue> parameters)
    {
        foreach (var item in parameters)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = item.Name;
            parameter.Value = item.Value ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
    }
}

public sealed class SqlCoreFilteredCountSqlServerLiveTests
{
    [Fact]
    public async Task RealSqlServer2022_CountFilterParity_WithNullsAndWindowPeerRows()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_SQLSERVER_CONNECTION required for SQL Server FILTER parity.");
            Assert.Skip("SQL Server 2022 live connection required.");
            return;
        }

        await using DbConnection connection = new Microsoft.Data.SqlClient.SqlConnection(cs);
        var token = TestContext.Current.CancellationToken;
        await connection.OpenAsync(token);
        var table = "sqlcore_fc_" + Guid.NewGuid().ToString("N");
        await SqlCoreFilteredCountLoweringTests.Execute(connection,
            $"CREATE TABLE {table} (id INT, grp INT, amount INT NULL, status INT)", [], token);
        try
        {
            await SqlCoreFilteredCountLoweringTests.Execute(connection,
                $"INSERT INTO {table} (id,grp,amount,status) VALUES (1,1,10,1),(2,1,NULL,1),(3,2,20,0),(4,2,NULL,1)", [], token);
            foreach (var (agg, predicate, window, expected) in SqlCoreFilteredCountLoweringTests.Cases())
            {
                var sql = SqlCoreFilteredCountLoweringTests.Query(agg, window, predicate, table);
                var compiled = SqlCoreFilteredCountLoweringTests.Compile(sql, SqlAgentToolType.MsSqlServer);
                var values = await SqlCoreFilteredCountLoweringTests.ReadCounts(
                    connection, compiled.Sql, compiled.Parameters, window, token);
                Assert.Equal(expected, values);
            }
        }
        finally
        {
            await SqlCoreFilteredCountLoweringTests.Execute(connection,
                "DROP TABLE " + table, [], token);
        }
    }
}
