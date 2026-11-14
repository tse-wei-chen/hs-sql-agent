using Admin.Service.Data;
using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.Firebird;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using FirebirdSql.Data.FirebirdClient;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Independent Firebird 5 physical target and persisted SQLite Admin Audit.
/// Setup/teardown DDL runs outside the approved Custom DML transaction.
/// </summary>
public sealed partial class FirebirdPhysicalLoadIntegrationTests
{
    private sealed class Fixture : IPhysicalLoadFixture
    {
        private readonly string _table;
        private readonly string _connection;
        private readonly string _auditPath;
        private readonly AdminContext _auditContext;
        private readonly IHttpContextAccessor _accessor;

        private Fixture(string table, string connection, string auditPath,
            AdminContext auditContext, IHttpContextAccessor accessor)
        {
            _table = table;
            _connection = connection;
            _auditPath = auditPath;
            _auditContext = auditContext;
            _accessor = accessor;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
            if (string.IsNullOrWhiteSpace(source))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Real Firebird 5 DML load proof requires TEST_FIREBIRD_CONNECTION.");
                Assert.Skip("Requires a real Firebird 5 database.");
            }

            var ct = TestContext.Current.CancellationToken;
            var connection = new FbConnectionStringBuilder(source)
            {
                Pooling = false
            }.ConnectionString;
            var table = "FL_" + Guid.NewGuid().ToString("N")[..15].ToUpperInvariant();
            var auditPath = Path.Combine(Path.GetTempPath(),
                "firebird-load-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");
            string? physicalDatabase;

            // Firebird startup may be delayed. Reuse the bounded readiness
            // retry proven for Firebird in PR #592.
            await using (var setup = new FbConnection(connection))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { await setup.OpenAsync(ct); break; }
                    catch (FbException) when (attempt < 24 && !ct.IsCancellationRequested)
                    { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                }
                await using (var create = setup.CreateCommand())
                {
                    create.CommandText = "CREATE TABLE " + table
                        + "(id INTEGER PRIMARY KEY, note VARCHAR(64) NOT NULL)";
                    await create.ExecuteNonQueryAsync(ct);
                }
                await using (var identity = setup.CreateCommand())
                {
                    identity.CommandText = "SELECT TRIM(MON$GUID) FROM MON$DATABASE";
                    physicalDatabase = (await identity.ExecuteScalarAsync(ct))?.ToString();
                }
                Assert.False(string.IsNullOrWhiteSpace(physicalDatabase));
            }

            var auditContext = await PhysicalLoadMcpFixtureSupport.CreateAuditContextAsync(
                auditPath, ct);
            var accessor = PhysicalLoadMcpFixtureSupport.CreateAccessor(
                "Firebird", connection, physicalDatabase, table,
                "firebird-load-mcp-request");
            return new Fixture(table, connection, auditPath, auditContext, accessor);
        }

        public CustomToolProxy CreateProxy(
            IDmlApprovalProvider approvalProvider, IAuditService? auditService = null,
            FirebirdProvider? targetProvider = null,
            ISqlExecutionConcurrencyLimiter? concurrencyLimiter = null)
        {
            var sql = "INSERT INTO " + _table
                + "(id,note) VALUES(100,'sensitive_note');"
                + "INSERT INTO " + _table
                + "(id,note) VALUES(101,'second_value')";
            return PhysicalLoadMcpFixtureSupport.CreateProxy(
                sql, SqlAgentToolType.Firebird, targetProvider ?? new FirebirdProvider(),
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
                approvalProvider, auditService, (FirebirdProvider?)targetProvider,
                concurrencyLimiter);

        public async Task<long> CountRowsAsync()
        {
            await using var witness = new FbConnection(_connection);
            await witness.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = witness.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + _table;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            await _auditContext.DisposeAsync();
            await using (var cleanup = new FbConnection(_connection))
            {
                await cleanup.OpenAsync(CancellationToken.None);
                await using var cmd = cleanup.CreateCommand();
                cmd.CommandText = "DROP TABLE " + _table;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            SqliteConnection.ClearAllPools();
            if (File.Exists(_auditPath)) File.Delete(_auditPath);
        }
    }


}
