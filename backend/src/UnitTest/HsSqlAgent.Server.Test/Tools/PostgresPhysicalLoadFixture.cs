using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class PostgresPhysicalLoadIntegrationTests
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

            var auditContext = new AdminContext(
                new DbContextOptionsBuilder<AdminContext>()
                    .UseSqlite(new SqliteConnectionStringBuilder
                    {
                        DataSource = auditPath
                    }.ToString()).Options);
            await auditContext.Database.EnsureCreatedAsync(ct);

            var http = new DefaultHttpContext
            {
                TraceIdentifier = "postgres-load-mcp-request"
            };
            http.Items[Common.Models.McpContextItemKeys.SqlProvider] = "Postgres";
            http.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = connection;
            http.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
            http.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
            http.Items[Common.Models.McpContextItemKeys.DatabaseName] =
                new NpgsqlConnectionStringBuilder(connection).Database;
            http.Items[Common.Models.McpContextItemKeys.AllowedTools] = "archive_order";
            http.Items[Common.Models.McpContextItemKeys.TableWhitelist] = schema + ".items";
            var accessor = new Mock<IHttpContextAccessor>();
            accessor.SetupGet(x => x.HttpContext).Returns(http);

            return new Fixture(schema, connection, auditPath, auditContext, accessor.Object);
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
            factory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres))
                .Returns(targetProvider ?? new PostgresProvider());
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

    private sealed class DecisionProvider(bool approve) : IDmlApprovalProvider
    {
        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(approve
                ? DmlApprovalResult.Approve(request, "postgres-load-prover")
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
            return DmlApprovalResult.Approve(request, "postgres-load-prover");
        }
    }
}
