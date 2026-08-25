using System.Data.Common;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlMySqlAssuredUpsertNativeTests
{
    [Fact]
    public async Task MySql84_SolePrimaryConflictExecutesButSecondUniqueKeyBlocksLowering()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var provider = new MySqlProvider();
        var table = "upsert_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = provider.CreateConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var schema = await ScalarStringAsync(
            connection,
            "SELECT DATABASE()",
            TestContext.Current.CancellationToken);
        var serverVersionText = await ScalarStringAsync(
            connection,
            "SELECT VERSION()",
            TestContext.Current.CancellationToken);
        var serverVersion = Version.Parse(serverVersionText.Split('-', 2)[0]);
        if (serverVersion.CompareTo(new Version(8, 0, 19)) < 0) return;

        try
        {
            await ExecuteAsync(
                connection,
                $"""
                CREATE TABLE `{table}` (
                    id bigint NOT NULL PRIMARY KEY,
                    name varchar(255) NOT NULL,
                    email varchar(255) NOT NULL
                )
                """,
                TestContext.Current.CancellationToken);
            await ExecuteAsync(
                connection,
                $"INSERT INTO `{table}` (id, name, email) VALUES (1, 'before', 'before@example.com')",
                TestContext.Current.CancellationToken);

            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO {table} (id, name, email) VALUES (1, 'after', 'after@example.com') " +
                "ON CONFLICT (id) DO UPDATE SET name = excluded.name, email = excluded.email",
                SqlAgentToolType.Postgres);
            var resolver = new DmlUniqueKeyResolver(provider);
            var soleResolution = await resolver.ResolveAsync(
                connectionString,
                schema,
                table,
                ["id"],
                TestContext.Current.CancellationToken);
            Assert.True(soleResolution.IsSoleEnforcedUniqueKey);

            var command = CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("policy-native-mysql-upsert-v1"),
                targetProfile: new SqlProviderCapabilityProfile(
                    SqlAgentToolType.MySQL,
                    ServerVersion: serverVersion),
                conflictTargetAssurance: soleResolution.ToConflictTargetAssurance());

            Assert.Contains("ON DUPLICATE KEY UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("AS `__core_proposed`", command.Sql, StringComparison.OrdinalIgnoreCase);
            await ExecuteCompiledAsync(
                connection,
                command,
                TestContext.Current.CancellationToken);

            var name = await ScalarStringAsync(
                connection,
                $"SELECT name FROM `{table}` WHERE id = 1",
                TestContext.Current.CancellationToken);
            var email = await ScalarStringAsync(
                connection,
                $"SELECT email FROM `{table}` WHERE id = 1",
                TestContext.Current.CancellationToken);
            Assert.Equal("after", name);
            Assert.Equal("after@example.com", email);

            await ExecuteAsync(
                connection,
                $"CREATE UNIQUE INDEX `uq_email_{table}` ON `{table}` (email)",
                TestContext.Current.CancellationToken);
            var multipleResolution = await resolver.ResolveAsync(
                connectionString,
                schema,
                table,
                ["id"],
                TestContext.Current.CancellationToken);
            Assert.False(multipleResolution.IsSoleEnforcedUniqueKey);
            Assert.Equal(2, multipleResolution.EnforcedKeys.Count);

            var error = Assert.Throws<SqlCompilationException>(() =>
                CoreDmlCompiler.CreateDefault().Compile(
                    parsed,
                    SqlAgentToolType.MySQL,
                    new SqlPlanValidationContext("policy-native-mysql-upsert-v1"),
                    targetProfile: new SqlProviderCapabilityProfile(
                        SqlAgentToolType.MySQL,
                        ServerVersion: serverVersion),
                    conflictTargetAssurance: multipleResolution.ToConflictTargetAssurance()));
            Assert.Contains("sole enforced", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ExecuteAsync(
                connection,
                $"DROP TABLE IF EXISTS `{table}`",
                TestContext.Current.CancellationToken);
        }
    }

    private static async Task ExecuteCompiledAsync(
        DbConnection connection,
        CompiledSqlCommand compiled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        foreach (var parameter in compiled.Parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<string> ScalarStringAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToString(value)
            ?? throw new InvalidOperationException($"Query '{sql}' returned null.");
    }
}
