using Admin.Service.Data;
using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.MySql;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class MySqlPhysicalLoadIntegrationTests
{
    private sealed class Fixture : IPhysicalLoadFixture
    {
        private readonly string _schema;
        private readonly string _connection;
        private readonly string _auditPath;
        private readonly AdminContext _auditContext;
        private readonly IHttpContextAccessor _accessor;

        private Fixture(string schema, string connection, string auditPath,
            AdminContext auditContext, IHttpContextAccessor accessor)
        {
            _schema = schema;
            _connection = connection;
            _auditPath = auditPath;
            _auditContext = auditContext;
            _accessor = accessor;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
            if (string.IsNullOrWhiteSpace(source))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Real MySQL 8.4 concurrency proof requires TEST_MYSQL_CONNECTION.");
                Assert.Skip("Requires a real MySQL 8.4 instance.");
            }

            var ct = TestContext.Current.CancellationToken;
            var adminConnection = new MySqlConnectionStringBuilder(source)
            {
                Database = string.Empty,
                Pooling = false
            }.ConnectionString;
            var schema = "load_" + Guid.NewGuid().ToString("N")[..16];
            var auditPath = Path.Combine(Path.GetTempPath(),
                "mysql-load-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");

            // MySQL schemas are databases: prepare physical InnoDB DDL before
            // any approved DML transaction (DDL otherwise implicitly commits).
            await using (var admin = new MySqlConnection(adminConnection))
            {
                await admin.OpenAsync(ct);
                await using var command = admin.CreateCommand();
                command.CommandText = "CREATE DATABASE " + schema;
                await command.ExecuteNonQueryAsync(ct);
            }
            var connection = new MySqlConnectionStringBuilder(source)
            {
                Database = schema,
                Pooling = false
            }.ConnectionString;
            await using (var setup = new MySqlConnection(connection))
            {
                await setup.OpenAsync(ct);
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE " + schema
                    + ".items(id INTEGER PRIMARY KEY,note VARCHAR(64) NOT NULL) ENGINE=InnoDB";
                await command.ExecuteNonQueryAsync(ct);
            }

            var auditContext = await PhysicalLoadMcpFixtureSupport.CreateAuditContextAsync(
                auditPath, ct);
            var accessor = PhysicalLoadMcpFixtureSupport.CreateAccessor(
                "MySQL", connection, new MySqlConnectionStringBuilder(connection).Database, schema + ".items",
                "mysql-load-mcp-request");
            return new Fixture(schema, connection, auditPath, auditContext, accessor);
        }

        public CustomToolProxy CreateProxy(
            IDmlApprovalProvider approvalProvider, IAuditService? auditService = null,
            MySqlProvider? targetProvider = null,
            ISqlExecutionConcurrencyLimiter? concurrencyLimiter = null)
        {
            var sql = "INSERT INTO " + _schema
                + ".items(id,note) VALUES(100,'sensitive_note');"
                + "INSERT INTO " + _schema
                + ".items(id,note) VALUES(101,'second_value')";
            return PhysicalLoadMcpFixtureSupport.CreateProxy(
                sql, SqlAgentToolType.MySQL, targetProvider ?? new MySqlProvider(),
                _accessor, _auditContext, approvalProvider, auditService,
                concurrencyLimiter);
        }

        // Only the shared physical load harness uses the provider-neutral
        // view. Keep this native fixture's public provider-specific method and
        // setup/teardown semantics unchanged.
        CustomToolProxy IPhysicalLoadFixture.CreateProxy(
            IDmlApprovalProvider approvalProvider,
            IAuditService? auditService,
            ISqlProvider? targetProvider,
            ISqlExecutionConcurrencyLimiter? concurrencyLimiter) =>
            CreateProxy(
                approvalProvider, auditService, (MySqlProvider?)targetProvider,
                concurrencyLimiter);

        public async Task<long> CountRowsAsync()
        {
            await using var independent = new MySqlConnection(_connection);
            await independent.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = independent.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM " + _schema + ".items";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            await _auditContext.DisposeAsync();
            // Remove the target DB from a separate administration connection.
            var adminConnection = new MySqlConnectionStringBuilder(_connection)
            {
                Database = string.Empty,
                Pooling = false
            }.ConnectionString;
            await using (var cleanup = new MySqlConnection(adminConnection))
            {
                await cleanup.OpenAsync(CancellationToken.None);
                await using var cmd = cleanup.CreateCommand();
                cmd.CommandText = "DROP DATABASE IF EXISTS " + _schema;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            SqliteConnection.ClearAllPools();
            if (File.Exists(_auditPath)) File.Delete(_auditPath);
        }
    }


}
