using Admin.Service.Data;
using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class PostgresPhysicalLoadIntegrationTests
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
            var source = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            if (string.IsNullOrWhiteSpace(source))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Real PostgreSQL 16 concurrency proof requires TEST_POSTGRES_CONNECTION.");
                Assert.Skip("Requires a real PostgreSQL 16 instance.");
            }

            var ct = TestContext.Current.CancellationToken;
            var connection = new NpgsqlConnectionStringBuilder(source)
            {
                Pooling = false
            }.ConnectionString;
            var schema = "load_" + Guid.NewGuid().ToString("N")[..16];
            var auditPath = Path.Combine(Path.GetTempPath(),
                "postgres-load-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");
            await using (var setup = new NpgsqlConnection(connection))
            {
                await setup.OpenAsync(ct);
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE SCHEMA " + schema + "; CREATE TABLE "
                    + schema + ".items(id INTEGER PRIMARY KEY,note TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(ct);
            }

            var auditContext = await PhysicalLoadMcpFixtureSupport.CreateAuditContextAsync(
                auditPath, ct);
            var accessor = PhysicalLoadMcpFixtureSupport.CreateAccessor(
                "Postgres", connection, new NpgsqlConnectionStringBuilder(connection).Database, schema + ".items",
                "postgres-load-mcp-request");
            return new Fixture(schema, connection, auditPath, auditContext, accessor);
        }

        public CustomToolProxy CreateProxy(
            IDmlApprovalProvider approvalProvider, IAuditService? auditService = null,
            PostgresProvider? targetProvider = null,
            ISqlExecutionConcurrencyLimiter? concurrencyLimiter = null)
        {
            var sql = "INSERT INTO " + _schema
                + ".items(id,note) VALUES(100,'sensitive_note');"
                + "INSERT INTO " + _schema
                + ".items(id,note) VALUES(101,'second_value')";
            return PhysicalLoadMcpFixtureSupport.CreateProxy(
                sql, SqlAgentToolType.Postgres, targetProvider ?? new PostgresProvider(),
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
                approvalProvider, auditService, (PostgresProvider?)targetProvider,
                concurrencyLimiter);

        public async Task<long> CountRowsAsync()
        {
            await using var independent = new NpgsqlConnection(_connection);
            await independent.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = independent.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM " + _schema + ".items";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            await _auditContext.DisposeAsync();
            await using (var cleanup = new NpgsqlConnection(_connection))
            {
                await cleanup.OpenAsync(CancellationToken.None);
                await using var cmd = cleanup.CreateCommand();
                cmd.CommandText = "DROP SCHEMA IF EXISTS " + _schema + " CASCADE";
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            SqliteConnection.ClearAllPools();
            if (File.Exists(_auditPath)) File.Delete(_auditPath);
        }
    }


}
