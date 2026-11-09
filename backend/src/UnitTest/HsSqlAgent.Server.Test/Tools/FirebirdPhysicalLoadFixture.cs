using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Firebird;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using FirebirdSql.Data.FirebirdClient;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Independent Firebird 5 physical target and persisted SQLite Admin Audit.
/// Setup/teardown DDL runs outside the approved Custom DML transaction.
/// </summary>
public sealed partial class FirebirdPhysicalLoadIntegrationTests
{
    private sealed class Fixture : IAsyncDisposable
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

            var auditContext = new AdminContext(
                new DbContextOptionsBuilder<AdminContext>()
                    .UseSqlite(new SqliteConnectionStringBuilder
                    {
                        DataSource = auditPath
                    }.ToString()).Options);
            await auditContext.Database.EnsureCreatedAsync(ct);

            var http = new DefaultHttpContext
            {
                TraceIdentifier = "firebird-load-mcp-request"
            };
            http.Items[Common.Models.McpContextItemKeys.SqlProvider] = "Firebird";
            http.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = connection;
            http.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
            http.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
            http.Items[Common.Models.McpContextItemKeys.DatabaseName] = physicalDatabase;
            http.Items[Common.Models.McpContextItemKeys.AllowedTools] = "archive_order";
            http.Items[Common.Models.McpContextItemKeys.TableWhitelist] = table;

            var accessor = new Mock<IHttpContextAccessor>();
            accessor.SetupGet(x => x.HttpContext).Returns(http);
            return new Fixture(table, connection, auditPath, auditContext, accessor.Object);
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
            factory.Setup(x => x.GetProvider(SqlAgentToolType.Firebird))
                .Returns(targetProvider ?? new FirebirdProvider());

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

    private sealed class DecisionProvider(bool approve) : IDmlApprovalProvider
    {
        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(approve
                ? DmlApprovalResult.Approve(request, "firebird-load-reviewer")
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
            return DmlApprovalResult.Approve(request, "firebird-load-reviewer");
        }
    }
}
