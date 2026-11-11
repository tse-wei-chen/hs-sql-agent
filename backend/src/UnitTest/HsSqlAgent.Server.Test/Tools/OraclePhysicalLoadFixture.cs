using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.Oracle;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Oracle.ManagedDataAccess.Client;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Independent Oracle 26ai physical target, backed by a separate SQLite Admin
/// Audit Store. DDL is outside the approved transaction because Oracle DDL
/// implicitly commits.
/// </summary>
public sealed partial class OraclePhysicalLoadIntegrationTests
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
            var source = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
            if (string.IsNullOrWhiteSpace(source))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Real Oracle 26ai DML load proof requires TEST_ORACLE_CONNECTION.");
                Assert.Skip("Requires a real Oracle 26ai database.");
            }

            var ct = TestContext.Current.CancellationToken;
            var connection = new OracleConnectionStringBuilder(source)
            {
                Pooling = false
            }.ConnectionString;
            var table = "OL_" + Guid.NewGuid().ToString("N")[..15].ToUpperInvariant();
            var auditPath = Path.Combine(Path.GetTempPath(),
                "oracle-load-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");
            string? physicalDatabase;

            // The Oracle Free PDB container may be starting. Use the same
            // bounded readiness retry as the independently proven #591 suite.
            await using (var setup = new OracleConnection(connection))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { await setup.OpenAsync(ct); break; }
                    catch (OracleException) when (attempt < 24 && !ct.IsCancellationRequested)
                    { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                }
                await using (var create = setup.CreateCommand())
                {
                    create.CommandText = "CREATE TABLE SQLCORE." + table
                        + "(id NUMBER(10,0) PRIMARY KEY, note VARCHAR2(64) NOT NULL)";
                    await create.ExecuteNonQueryAsync(ct);
                }
                await using (var identity = setup.CreateCommand())
                {
                    identity.CommandText = "SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM DUAL";
                    physicalDatabase = (await identity.ExecuteScalarAsync(ct))?.ToString();
                }
                Assert.False(string.IsNullOrWhiteSpace(physicalDatabase));
            }

            var auditContext = new AdminContext(
                new DbContextOptionsBuilder<AdminContext>()
                    .UseSqlite(new SqliteConnectionStringBuilder
                    {
                        DataSource = auditPath
                    }.ToString()).Options);
            await auditContext.Database.EnsureCreatedAsync(ct);

            var http = new DefaultHttpContext
            {
                TraceIdentifier = "oracle-load-mcp-request"
            };
            http.Items[Common.Models.McpContextItemKeys.SqlProvider] = "Oracle";
            http.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = connection;
            http.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
            http.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
            http.Items[Common.Models.McpContextItemKeys.DatabaseName] = physicalDatabase;
            http.Items[Common.Models.McpContextItemKeys.AllowedTools] = "archive_order";
            http.Items[Common.Models.McpContextItemKeys.TableWhitelist] = "SQLCORE." + table;

            var accessor = new Mock<IHttpContextAccessor>();
            accessor.SetupGet(x => x.HttpContext).Returns(http);
            return new Fixture(table, connection, auditPath, auditContext, accessor.Object);
        }

        public CustomToolProxy CreateProxy(
            IDmlApprovalProvider approvalProvider, IAuditService? auditService = null,
            OracleProvider? targetProvider = null,
            ISqlExecutionConcurrencyLimiter? concurrencyLimiter = null)
        {
            var sql = "INSERT INTO SQLCORE." + _table
                + "(id,note) VALUES(100,'sensitive_note');"
                + "INSERT INTO SQLCORE." + _table
                + "(id,note) VALUES(101,'second_value')";
            var tools = new Mock<ICustomSqlToolService>();
            tools.Setup(x => x.GetPublishedToolByNameAsync(
                    "archive_order", 42, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CustomSqlTool
                {
                    Name = "archive_order",
                    Type = "DML",
                    SqlTemplate = sql
                });
            var factory = new Mock<ISqlProviderFactory>();
            factory.Setup(x => x.GetProvider(SqlAgentToolType.Oracle))
                .Returns(targetProvider ?? new OracleProvider());

            var state = new SecurityPolicyRuntimeState();
            state.SetCurrent(new SecurityPolicyModel
            {
                DmlMaxAffectedRows = 16,
                MaxConcurrentSql = 1,
                RequireWhereForUpdate = true,
                RequireWhereForDelete = true
            });
            return new CustomToolProxy(
                "archive_order", tools.Object, _accessor, factory.Object,
                auditService ?? new AuditService(_auditContext, _accessor),
                Mock.Of<IQueryValueParserService>(),
                state,
                concurrencyLimiter ?? new SqlExecutionConcurrencyLimiter(state),
                dmlApprovalProvider: approvalProvider);
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
                approvalProvider, auditService, (OracleProvider?)targetProvider,
                concurrencyLimiter);

        public async Task<long> CountRowsAsync()
        {
            await using var witness = new OracleConnection(_connection);
            await witness.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = witness.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM SQLCORE." + _table;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            await _auditContext.DisposeAsync();
            await using (var cleanup = new OracleConnection(_connection))
            {
                await cleanup.OpenAsync(CancellationToken.None);
                await using var cmd = cleanup.CreateCommand();
                cmd.CommandText = "DROP TABLE SQLCORE." + _table + " PURGE";
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            SqliteConnection.ClearAllPools();
            if (File.Exists(_auditPath)) File.Delete(_auditPath);
        }
    }


}
