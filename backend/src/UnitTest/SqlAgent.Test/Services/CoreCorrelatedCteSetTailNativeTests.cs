using System.Data.Common;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCorrelatedCteSetTailNativeTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "TEST_POSTGRES_CONNECTION")]
    [InlineData(SqlAgentToolType.MySQL, "TEST_MYSQL_CONNECTION")]
    public async Task Native_CorrelatedScalarRootCteSetTail_PreservesOuterScope(
        SqlAgentToolType targetProvider,
        string connectionEnvironmentVariable)
    {
        var connectionString = Environment.GetEnvironmentVariable(connectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var outerTable = "cte_outer_" + suffix;
        var archiveTable = "cte_archive_" + suffix;

        await using DbConnection connection = targetProvider switch
        {
            SqlAgentToolType.Postgres => new PostgresProvider().CreateConnection(connectionString),
            SqlAgentToolType.MySQL => new MySqlProvider().CreateConnection(connectionString),
            _ => throw new InvalidOperationException($"Unsupported native test provider {targetProvider}.")
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        try
        {
            await ExecuteAsync(
                connection,
                $"CREATE TABLE {outerTable} (id bigint NOT NULL PRIMARY KEY)",
                TestContext.Current.CancellationToken);
            await ExecuteAsync(
                connection,
                $"CREATE TABLE {archiveTable} (id bigint NOT NULL, tenant_id bigint NOT NULL)",
                TestContext.Current.CancellationToken);
            await ExecuteAsync(
                connection,
                $"INSERT INTO {outerTable} (id) VALUES (1), (3)",
                TestContext.Current.CancellationToken);
            await ExecuteAsync(
                connection,
                $"INSERT INTO {archiveTable} (id, tenant_id) VALUES (1, 7), (3, 7), (9, 8)",
                TestContext.Current.CancellationToken);

            var parsed = CoreSqlTextParser.ParseQuery(
                $"SELECT u.id, (" +
                $"WITH active AS (SELECT id FROM {archiveTable} WHERE tenant_id = 7) " +
                "SELECT a.id FROM active AS a WHERE a.id <= u.id " +
                "UNION ALL SELECT u.id FROM active AS b WHERE b.id = u.id " +
                "ORDER BY 1 DESC LIMIT 1) AS picked " +
                $"FROM {outerTable} AS u ORDER BY u.id",
                SqlAgentToolType.Postgres);
            var compiled = CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                targetProvider,
                new SqlPlanValidationContext("policy-native-correlated-cte-set-v1"),
                new SqlExecutionPlanPolicy(0));

            Assert.Contains("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UNION ALL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ORDER BY 1 DESC", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_set", compiled.Sql, StringComparison.OrdinalIgnoreCase);

            var rows = await ReadRowsAsync(
                connection,
                compiled,
                TestContext.Current.CancellationToken);
            Assert.Equal(new[] { (1L, 1L), (3L, 3L) }, rows);
        }
        finally
        {
            await ExecuteAsync(
                connection,
                $"DROP TABLE IF EXISTS {archiveTable}",
                TestContext.Current.CancellationToken);
            await ExecuteAsync(
                connection,
                $"DROP TABLE IF EXISTS {outerTable}",
                TestContext.Current.CancellationToken);
        }
    }

    private static async Task<List<(long Id, long Picked)>> ReadRowsAsync(
        DbConnection connection,
        CompiledSqlCommand compiled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        AddParameters(command, compiled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(long Id, long Picked)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                Convert.ToInt64(reader.GetValue(0)),
                Convert.ToInt64(reader.GetValue(1))));
        }
        return rows;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(DbCommand command, CompiledSqlCommand compiled)
    {
        foreach (var parameter in compiled.Parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }
    }
}
