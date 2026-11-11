using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class SqlServerPhysicalLoadIntegrationTests
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
            var source = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
            if (string.IsNullOrWhiteSpace(source))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Real SQL Server 2022 concurrency proof requires TEST_SQLSERVER_CONNECTION.");
                Assert.Skip("Requires real SQL Server 2022.");
            }

            var ct = TestContext.Current.CancellationToken;
            var connection = new SqlConnectionStringBuilder(source)
            {
                Pooling = false
            }.ConnectionString;
            var table = "load_" + Guid.NewGuid().ToString("N")[..16];
            var auditPath = Path.Combine(Path.GetTempPath(),
                "sqlserver-load-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");

            // SQL Server 2022 CI container needs a startup readiness retry.
            // Isolated dbo table has a random name; target DML is only INSERT.
            await using (var setup = new SqlConnection(connection))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { await setup.OpenAsync(ct); break; }
                    catch (SqlException) when (attempt < 24 && !ct.IsCancellationRequested)
                    { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                }
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE dbo." + table
                    + "(id INTEGER PRIMARY KEY, note NVARCHAR(64) NOT NULL)";
                await command.ExecuteNonQueryAsync(ct);
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
                TraceIdentifier = "sqlserver-load-mcp-request"
            };
            http.Items[Common.Models.McpContextItemKeys.SqlProvider] = "MsSqlServer";
            http.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = connection;
            http.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
            http.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
            http.Items[Common.Models.McpContextItemKeys.DatabaseName] =
                new SqlConnectionStringBuilder(connection).InitialCatalog;
            http.Items[Common.Models.McpContextItemKeys.AllowedTools] = "archive_order";
            http.Items[Common.Models.McpContextItemKeys.TableWhitelist] = "dbo." + table;
            var accessor = new Mock<IHttpContextAccessor>();
            accessor.SetupGet(x => x.HttpContext).Returns(http);

            return new Fixture(table, connection, auditPath, auditContext, accessor.Object);
        }

        public CustomToolProxy CreateProxy(
            IDmlApprovalProvider approvalProvider, IAuditService? auditService = null,
            MsSqlServerProvider? targetProvider = null,
            ISqlExecutionConcurrencyLimiter? concurrencyLimiter = null)
        {
            var sql = "INSERT INTO dbo." + _table
                + "(id,note) VALUES(100,'sensitive_note');"
                + "INSERT INTO dbo." + _table
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
            factory.Setup(x => x.GetProvider(SqlAgentToolType.MsSqlServer))
                .Returns(targetProvider ?? new MsSqlServerProvider());
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
                approvalProvider, auditService, (MsSqlServerProvider?)targetProvider,
                concurrencyLimiter);

        public async Task<long> CountRowsAsync()
        {
            await using var witness = new SqlConnection(_connection);
            await witness.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = witness.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM dbo." + _table;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            await _auditContext.DisposeAsync();
            await using (var cleanup = new SqlConnection(_connection))
            {
                await cleanup.OpenAsync(CancellationToken.None);
                await using var cmd = cleanup.CreateCommand();
                cmd.CommandText = "DROP TABLE IF EXISTS dbo." + _table;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            SqliteConnection.ClearAllPools();
            if (File.Exists(_auditPath)) File.Delete(_auditPath);
        }
    }


}
