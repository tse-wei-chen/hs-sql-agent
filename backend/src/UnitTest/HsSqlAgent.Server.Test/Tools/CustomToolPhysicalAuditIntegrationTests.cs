using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Independent physical target and Audit Store. Do not treat a mock result
/// or a successful HTTP/MCP response as proof of a committed mutation.
/// </summary>
public sealed partial class CustomToolPhysicalAuditIntegrationTests
{
    [Fact]
    public async Task ApprovedMultiStatementDml_PersistsMatchingApprovalIdAndPhysicalRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = new DecisionProvider(approve: true);
        var proxy = fixture.CreateProxy(approval);

        var result = await proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Success", result, StringComparison.Ordinal);
        Assert.Equal(2L, await fixture.CountRowsAsync());
        Assert.NotNull(approval.LastRequest);
        Assert.Equal(2, approval.LastRequest.Statements.Count);

        // Read back from disk using a separate EF context, not the tracked
        // entity or the in-memory event presented to the Audit service.
        await using var persisted = fixture.OpenAuditContext();
        var audit = await persisted.AuditLogs.AsNoTracking().SingleAsync(
            x => x.Action == "mcp.archive_order.executed",
            TestContext.Current.CancellationToken);
        Assert.Equal("success", audit.Result);
        Assert.Equal("approved", audit.ApprovalStatus);
        Assert.Equal(2, audit.AffectedRows);
        Assert.Equal("incoming-mcp-request", audit.RequestId);
        Assert.Equal(7, audit.AccessKeyId);
        Assert.Equal(42, audit.DbManagementId);
        using var definition = JsonDocument.Parse(Assert.IsType<string>(audit.Definition));
        Assert.Equal(approval.LastRequest.RequestId,
            definition.RootElement.GetProperty("ApprovalRequestId").GetString());
        Assert.DoesNotContain("sensitive_note", audit.Definition, StringComparison.Ordinal);
        Assert.DoesNotContain("second_value", audit.Definition, StringComparison.Ordinal);
        var searchable = await new AuditService(
                persisted, Mock.Of<IHttpContextAccessor>())
            .QueryAsync(new AuditLogFilter
            {
                Action = "mcp.archive_order.executed",
                Keyword = approval.LastRequest.RequestId
            }, TestContext.Current.CancellationToken);
        Assert.Single(searchable.Items);
        Assert.Equal(audit.EventId, searchable.Items.Single().EventId);
    }

    [Fact]
    public async Task RejectedDml_NoPhysicalWritesAndPersistedAuditIdentifiesApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = new DecisionProvider(approve: false);
        var proxy = fixture.CreateProxy(approval);

        var result = await proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("review declined", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await fixture.CountRowsAsync());
        Assert.NotNull(approval.LastRequest);

        await using var persisted = fixture.OpenAuditContext();
        var audit = await persisted.AuditLogs.AsNoTracking().SingleAsync(
            x => x.Action == "mcp.archive_order.executed",
            TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", audit.Result);
        Assert.Equal("declined", audit.ApprovalStatus);
        using var definition = JsonDocument.Parse(Assert.IsType<string>(audit.Definition));
        Assert.Equal(approval.LastRequest.RequestId,
            definition.RootElement.GetProperty("ApprovalRequestId").GetString());
    }

    [Fact]
    public async Task AuditFailureAfterPhysicalCommit_DoesNotReportFailureOrReplayMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var brokenAudit = new Mock<IAuditService>();
        brokenAudit.Setup(x => x.WriteEventAsync(
                "mcp.archive_order.executed", "archive_order", "success",
                It.IsAny<AuditEventContext>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Admin Audit Store connection failed."));
        var approval = new DecisionProvider(approve: true);
        var proxy = fixture.CreateProxy(approval, brokenAudit.Object);

        var result = await proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Success", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Error:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2L, await fixture.CountRowsAsync());
        Assert.NotNull(approval.LastRequest);
        brokenAudit.Verify(x => x.WriteEventAsync(
            "mcp.archive_order.executed", "archive_order", "success",
            It.IsAny<AuditEventContext>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class DecisionProvider(bool approve) : IDmlApprovalProvider
    {
        public DmlApprovalRequest? LastRequest { get; private set; }

        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(approve
                ? DmlApprovalResult.Approve(request, "audit-test-reviewer")
                : DmlApprovalResult.Reject(request, "Review declined."));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string MutationSql =
            "INSERT INTO main.items (id, note) VALUES (100, 'sensitive_note');" +
            "INSERT INTO main.items (id, note) VALUES (101, 'second_value')";
        private readonly string _targetPath;
        private readonly string _auditPath;
        private readonly SqliteProvider _sqlProvider = new();
        private readonly AdminContext _auditContext;
        private readonly IHttpContextAccessor _accessor;
        private readonly string _targetConnectionString;

        private Fixture(string targetPath, string auditPath, AdminContext auditContext,
            string targetConnectionString, IHttpContextAccessor accessor)
        {
            _targetPath = targetPath;
            _auditPath = auditPath;
            _auditContext = auditContext;
            _targetConnectionString = targetConnectionString;
            _accessor = accessor;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var targetPath = Path.Combine(Path.GetTempPath(),
                "hsa-custom-dml-target-" + Guid.NewGuid().ToString("N") + ".db");
            var auditPath = Path.Combine(Path.GetTempPath(),
                "hsa-custom-dml-audit-" + Guid.NewGuid().ToString("N") + ".db");
            var provider = new SqliteProvider();
            var target = provider.BuildConnectionString(
                new BuildDbConnectionModelBase { Database = targetPath });

            await using (var connection = new SqliteConnection(target))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "CREATE TABLE items (id INTEGER PRIMARY KEY, note TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var auditContext = OpenAuditContext(auditPath);
            await auditContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var httpContext = new DefaultHttpContext { TraceIdentifier = "incoming-mcp-request" };
            httpContext.Items[Common.Models.McpContextItemKeys.SqlProvider] = "Sqlite";
            httpContext.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = target;
            httpContext.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
            httpContext.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
            httpContext.Items[Common.Models.McpContextItemKeys.DatabaseName] = "main";
            httpContext.Items[Common.Models.McpContextItemKeys.AllowedTools] = "archive_order";
            httpContext.Items[Common.Models.McpContextItemKeys.TableWhitelist] = "main.items";
            var accessorMock = new Mock<IHttpContextAccessor>();
            accessorMock.SetupGet(x => x.HttpContext).Returns(httpContext);
            var accessor = accessorMock.Object;
            return new Fixture(targetPath, auditPath, auditContext, target, accessor);
        }

        private static AdminContext OpenAuditContext(string file) =>
            new(new DbContextOptionsBuilder<AdminContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = file }.ToString())
                .Options);

        public AdminContext OpenAuditContext() => OpenAuditContext(_auditPath);

        public string TargetConnectionString => _targetConnectionString;

        public CustomToolProxy CreateProxy(
            IDmlApprovalProvider approvalProvider, IAuditService? auditService = null,
            SqliteProvider? targetProvider = null,
            ISqlExecutionConcurrencyLimiter? concurrencyLimiter = null)
        {
            var publishedTools = new Mock<ICustomSqlToolService>();
            publishedTools.Setup(x => x.GetPublishedToolByNameAsync(
                    "archive_order", 42, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CustomSqlTool
                {
                    Name = "archive_order", Type = "DML", SqlTemplate = MutationSql
                });
            var providerFactory = new Mock<ISqlProviderFactory>();
            providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Sqlite))
                .Returns(targetProvider ?? _sqlProvider);
            var policy = new SecurityPolicyRuntimeState();
            policy.SetCurrent(new SecurityPolicyModel
            {
                DmlMaxAffectedRows = 16,
                MaxConcurrentSql = 1,
                RequireWhereForUpdate = true,
                RequireWhereForDelete = true
            });
            return new CustomToolProxy(
                "archive_order",
                publishedTools.Object,
                _accessor,
                providerFactory.Object,
                auditService ?? new AuditService(_auditContext, _accessor),
                Mock.Of<IQueryValueParserService>(),
                policy,
                concurrencyLimiter ?? new SqlExecutionConcurrencyLimiter(policy),
                dmlApprovalProvider: approvalProvider);
        }

        public async Task<long> CountRowsAsync()
        {
            await using var connection = new SqliteConnection(_targetConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM items";
            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async ValueTask DisposeAsync()
        {
            await _auditContext.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_auditPath)) File.Delete(_auditPath);
            if (File.Exists(_targetPath)) File.Delete(_targetPath);
        }
    }
}
