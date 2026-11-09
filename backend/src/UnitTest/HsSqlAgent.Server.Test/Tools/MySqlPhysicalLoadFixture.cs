using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.MySql;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using MySql.Data.MySqlClient;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class MySqlPhysicalLoadIntegrationTests
{
    private sealed class Fixture : IAsyncDisposable
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

            var auditContext = new AdminContext(
                new DbContextOptionsBuilder<AdminContext>()
                    .UseSqlite(new SqliteConnectionStringBuilder
                    {
                        DataSource = auditPath
                    }.ToString()).Options);
            await auditContext.Database.EnsureCreatedAsync(ct);

            var http = new DefaultHttpContext
            {
                TraceIdentifier = "mysql-load-mcp-request"
            };
            http.Items[Common.Models.McpContextItemKeys.SqlProvider] = "MySQL";
            http.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = connection;
            http.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
            http.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
            http.Items[Common.Models.McpContextItemKeys.DatabaseName] =
                new MySqlConnectionStringBuilder(connection).Database;
            http.Items[Common.Models.McpContextItemKeys.AllowedTools] = "archive_order";
            http.Items[Common.Models.McpContextItemKeys.TableWhitelist] = schema + ".items";
            var accessor = new Mock<IHttpContextAccessor>();
            accessor.SetupGet(x => x.HttpContext).Returns(http);

            return new Fixture(schema, connection, auditPath, auditContext, accessor.Object);
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
            factory.Setup(x => x.GetProvider(SqlAgentToolType.MySQL))
                .Returns(targetProvider ?? new MySqlProvider());
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

    private sealed class DecisionProvider(bool approve) : IDmlApprovalProvider
    {
        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(approve
                ? DmlApprovalResult.Approve(request, "mysql-load-prover")
                : DmlApprovalResult.Reject(request, "Load test rejected."));
    }

    private sealed class CallbackApprovalProvider(Func<Task> beforeApproval)
        : IDmlApprovalProvider
    {
        public DmlApprovalRequest? LastRequest { get; private set; }

        public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await beforeApproval();
            return DmlApprovalResult.Approve(request, "mysql-load-prover");
        }
    }
}
