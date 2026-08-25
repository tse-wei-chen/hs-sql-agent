using System.Data.Common;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreNullOrderingNativeTests
{
    [Fact]
    public async Task MySql_NativeInverseNullOrderingOnDirectColumn_PreservesExplicitOrder()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var table = "null_order_" + suffix;
        await using var connection = new MySqlProvider().CreateConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        try
        {
            await ExecuteAsync(
                connection,
                $"CREATE TABLE {table} (id bigint NOT NULL PRIMARY KEY, score int NULL)",
                TestContext.Current.CancellationToken);
            await ExecuteAsync(
                connection,
                $"INSERT INTO {table} (id, score) VALUES (1, NULL), (2, 2), (3, 1)",
                TestContext.Current.CancellationToken);

            var ascending = Compile(
                $"SELECT score FROM {table} ORDER BY score ASC NULLS LAST");
            Assert.Contains("CASE", ascending.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IS NULL", ascending.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NULLS LAST", ascending.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                new int?[] { 1, 2, null },
                await ReadNullableIntsAsync(
                    connection,
                    ascending,
                    TestContext.Current.CancellationToken));

            var descending = Compile(
                $"SELECT score FROM {table} ORDER BY score DESC NULLS FIRST");
            Assert.Contains("CASE", descending.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IS NULL", descending.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NULLS FIRST", descending.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                new int?[] { null, 2, 1 },
                await ReadNullableIntsAsync(
                    connection,
                    descending,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await ExecuteAsync(
                connection,
                $"DROP TABLE IF EXISTS {table}",
                TestContext.Current.CancellationToken);
        }
    }

    private static CompiledSqlCommand Compile(string sql) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-native-null-order-v1"),
            new SqlExecutionPlanPolicy());

    private static async Task<List<int?>> ReadNullableIntsAsync(
        DbConnection connection,
        CompiledSqlCommand compiled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        AddParameters(command, compiled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<int?>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.IsDBNull(0)
                ? null
                : Convert.ToInt32(reader.GetValue(0)));
        }
        return values;
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
