using System.Data.Common;
using HsSqlAgent.SqlCore;
using MySql.Data.MySqlClient;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G10/Q02 DISTINCT filtered COUNT uses the existing F# FILTER graph.
/// DISTINCT window aggregation is deliberately never lowered.
/// </summary>
public sealed class SqlCoreFilteredDistinctCountTests
{
    private const string Aggregate = "COUNT(DISTINCT amount)";
    private static readonly (string Filter, long[] ByGroup, long Total)[] Scenarios =
    [
        ("status = 1", [2, 1, 0], 3),
        ("status = 0", [0, 1, 1], 2),
        ("status IS NULL", [0, 0, 1], 1),
        ("status = 99", [0, 0, 0], 0),
        ("status = 1 OR status IS NULL", [2, 1, 1], 3)
    ];

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(DISTINCT CASE WHEN")]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT_BIG(DISTINCT CASE WHEN")]
    public void DistinctFilteredCount_UsesOneValuePerMatchingRow(
        SqlAgentToolType target, string rendered)
    {
        var command = Compile(GroupQuery("orders", "status = 1"), target);
        Assert.Contains(rendered, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THEN", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("amount", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("END)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUM(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Matrix_DeclaresTheDistinctCountBoundary(SqlAgentToolType provider)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);
        var capability = Assert.Single(matrix.Capabilities,
            value => value.Id == "aggregate.filtered_distinct_count");
        var general = Assert.Single(matrix.Capabilities,
            value => value.Id == "expression.filter");
        if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
        {
            Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
            Assert.Equal(SqlCapabilityStatus.Rejected, general.Status);
        }
        else
            Assert.Equal(general.Status, capability.Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void DistinctWindowAggregate_RemainsRejectedBeforeLowering(SqlAgentToolType target)
    {
        var sql = "SELECT COUNT(DISTINCT amount) FILTER (WHERE status = 1) " +
                  "OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) " +
                  "FROM orders";
        var error = Assert.Throws<SqlCompilationException>(() => Compile(sql, target));
        Assert.Contains("DISTINCT window aggregate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(DISTINCT amount + 1)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(DISTINCT amount + 1)")]
    [InlineData(SqlAgentToolType.MySQL, "SUM(DISTINCT amount)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SUM(DISTINCT amount)")]
    [InlineData(SqlAgentToolType.MySQL, "COUNT(DISTINCT 1)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "COUNT(DISTINCT 1)")]
    public void DistinctOutsideDirectColumn_IsNotAuthorized(
        SqlAgentToolType target, string aggregate)
    {
        var sql = "SELECT " + aggregate + " FILTER (WHERE status = 1) FROM orders";
        var error = Assert.Throws<SqlCompilationException>(() => Compile(sql, target));
        Assert.Contains("expression.filter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "status + 1 = 2")]
    [InlineData(SqlAgentToolType.MsSqlServer, "status + 1 = 2")]
    [InlineData(SqlAgentToolType.MySQL, "EXISTS (SELECT 1)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "EXISTS (SELECT 1)")]
    public void DistinctPredicate_CannotBypassBoundedProof(
        SqlAgentToolType target, string predicate)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(GroupQuery("orders", predicate), target));
        Assert.Contains("expression.filter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void InvalidSourceDistinctFilter_RemainsRejected(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(GroupQuery("orders", "status = 1"),
                source, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("g10-distinct-source-proof"),
                new SqlExecutionPlanPolicy()));
        Assert.Contains("source dialect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public async Task RealPostgresAndMySql_AllDistinctScenariosMatchExpectedRows(
        SqlAgentToolType target)
    {
        var env = target == SqlAgentToolType.Postgres
            ? "TEST_POSTGRES_CONNECTION" : "TEST_MYSQL_CONNECTION";
        var cs = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException(env + " required for live DISTINCT FILTER parity");
            Assert.Skip(env + " required for live parity");
            return;
        }

        await using DbConnection db = target == SqlAgentToolType.Postgres
            ? new NpgsqlConnection(cs) : new MySqlConnection(cs);
        var token = TestContext.Current.CancellationToken;
        await db.OpenAsync(token);
        var table = "sqlcore_fd_" + Guid.NewGuid().ToString("N");
        await SqlCoreFilteredCountLoweringTests.Execute(db,
            (target == SqlAgentToolType.Postgres ? "CREATE TEMP TABLE " : "CREATE TEMPORARY TABLE ")
            + table + " (id INT, grp INT, amount INT NULL, status INT NULL)", [], token);
        await InsertFixture(db, table, token);
        await CheckAll(db, target, table, token);
    }

    internal static string GroupQuery(string table, string predicate) =>
        "SELECT grp, " + Aggregate + " FILTER (WHERE " + predicate +
        ") AS n FROM " + table + " GROUP BY grp ORDER BY grp";

    private static string TotalQuery(string table, string predicate) =>
        "SELECT " + Aggregate + " FILTER (WHERE " + predicate + ") FROM " + table;

    internal static CompiledSqlCommand Compile(string sql, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("g10-distinct-filter-v1"),
            new SqlExecutionPlanPolicy());

    internal static async Task InsertFixture(DbConnection db, string table,
        CancellationToken token)
    {
        await SqlCoreFilteredCountLoweringTests.Execute(db,
            "INSERT INTO " + table +
            " (id,grp,amount,status) VALUES " +
            "(1,1,10,1),(2,1,10,1),(3,1,NULL,1),(4,1,20,1)," +
            "(5,2,10,0),(6,2,30,1),(7,2,30,1),(8,2,NULL,NULL)," +
            "(9,3,20,0),(10,3,20,NULL)", [], token);
    }

    internal static async Task CheckAll(DbConnection db, SqlAgentToolType target,
        string table, CancellationToken token)
    {
        foreach (var (filter, byGroup, total) in Scenarios)
        {
            var group = Compile(GroupQuery(table, filter), target);
            Assert.Equal(byGroup,
                await ReadColumn(db, group, 1, token));
            var global = Compile(TotalQuery(table, filter), target);
            Assert.Equal([total],
                await ReadColumn(db, global, 0, token));
        }
    }

    private static async Task<long[]> ReadColumn(DbConnection db,
        CompiledSqlCommand compiled, int ordinal, CancellationToken token)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var item in compiled.Parameters)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = item.Name;
            parameter.Value = item.Value ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
        var results = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            results.Add(Convert.ToInt64(reader.GetValue(ordinal)));
        return results.ToArray();
    }
}

/// <summary>Uses the existing isolated SQL Server 2022 Actions service.</summary>
public sealed class SqlCoreFilteredDistinctCountSqlServerLiveTests
{
    [Fact]
    public async Task SqlServer2022RealDistinctCountsAgreeWithPostgresReference()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TEST_SQLSERVER_CONNECTION required for live DISTINCT parity");
            Assert.Skip("TEST_SQLSERVER_CONNECTION required");
            return;
        }

        await using DbConnection db = new Microsoft.Data.SqlClient.SqlConnection(cs);
        var token = TestContext.Current.CancellationToken;
        await db.OpenAsync(token);
        var table = "sqlcore_fd_" + Guid.NewGuid().ToString("N");
        await SqlCoreFilteredCountLoweringTests.Execute(db,
            "CREATE TABLE " + table + " (id INT, grp INT, amount INT NULL, status INT NULL)", [], token);
        try
        {
            await SqlCoreFilteredDistinctCountTests.InsertFixture(db, table, token);
            await SqlCoreFilteredDistinctCountTests.CheckAll(
                db, SqlAgentToolType.MsSqlServer, table, token);
        }
        finally
        {
            await SqlCoreFilteredCountLoweringTests.Execute(db,
                "DROP TABLE " + table, [], token);
        }
    }
}
