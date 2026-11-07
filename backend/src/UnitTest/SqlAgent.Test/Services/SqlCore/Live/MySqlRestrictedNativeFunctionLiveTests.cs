using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using MySql.Data.MySqlClient;
using SqlAgent.Test.Strategies;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real MySQL 8 function read of an excluded relation, followed by the
/// restricted F# compiler policy. Direct DBA-side execution is a control:
/// the compiler must reject the same name before it reaches MySQL.
/// </summary>
public sealed class MySqlRestrictedNativeFunctionLiveTests(MySqlFixture fixture)
    : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task StoredFunction_CanReadExcludedPhysicalTable_ButWhitelistedQueryCannotCallIt()
    {
        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var secretTable = "hsql_secret_" + suffix;
        var functionName = "hsql_leak_" + suffix;
        const string database = "test_db";
        var secretName = "`" + database + "`.`" + secretTable + "`";
        var qualifiedFunction = "`" + database + "`.`" + functionName + "`";
        var leakedValue = "HSQL_PRIVATE_SENTINEL_" + suffix;
        await using var connection = new MySqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);

        async Task ExecuteAsync(string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(token);
        }

        await ExecuteAsync($"CREATE TABLE {secretName} (id INT PRIMARY KEY, secret VARCHAR(100) NOT NULL)");
        try
        {
            await ExecuteAsync($"INSERT INTO {secretName} VALUES (1, '{leakedValue}')");
            await ExecuteAsync(
                $"CREATE FUNCTION {qualifiedFunction}() RETURNS VARCHAR(100) " +
                $"DETERMINISTIC READS SQL DATA RETURN (SELECT secret FROM {secretName} WHERE id = 1)");
            try
            {
                await using (var control = new MySqlCommand(
                    $"SELECT {qualifiedFunction}()", connection))
                {
                    var physical = (await control.ExecuteScalarAsync(token))?.ToString();
                    Assert.Equal(leakedValue, physical);
                }

                var query = $"SELECT {qualifiedFunction}() AS leaked FROM `{database}`.`users`";
                var context = new SqlPlanValidationContext(
                    "physical-native-function-whitelist-v1",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { database + ".users" });
                var error = Assert.Throws<UnauthorizedAccessException>(() =>
                    SqlCoreFacade.CompileQuery(
                        query,
                        SqlAgentToolType.MySQL,
                        SqlAgentToolType.MySQL,
                        context,
                        new SqlExecutionPlanPolicy(10)));
                Assert.Contains("not proven", error.Message, StringComparison.OrdinalIgnoreCase);
                var diagnostic = Assert.IsType<SqlDiagnostic>(
                    error.Data["HsSqlAgent.SqlCore.Diagnostic"]);
                Assert.Equal("SQL_POLICY_NATIVE_QUERY_FUNCTION_NOT_PROVEN", diagnostic.Code);
                Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
                Assert.DoesNotContain(leakedValue, error.Message);
                Assert.NotEmpty(SqlCoreFacade.CompileQuery(
                    "SELECT ABS(id) AS safe_id FROM test_db.users",
                    SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
                    context, new SqlExecutionPlanPolicy(10)).Sql);
            }
            finally
            {
                await ExecuteAsync($"DROP FUNCTION IF EXISTS {qualifiedFunction}");
            }
        }
        finally
        {
            await ExecuteAsync($"DROP TABLE IF EXISTS {secretName}");
        }
    }
}
