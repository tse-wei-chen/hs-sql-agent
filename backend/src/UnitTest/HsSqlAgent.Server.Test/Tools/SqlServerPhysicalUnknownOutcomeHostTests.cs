using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Common.Models;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Real SQL Server 2022 target + independent persistent SQLite Admin Audit.
/// The published Custom MCP proxy, approval, compiler and executor are real.
/// </summary>
public sealed partial class SqlServerPhysicalUnknownOutcomeIntegrationTests
{
    [Theory]
    [InlineData(true, "dml.commit_outcome_unknown", "DmlCommitOutcomeUnknown", 2L)]
    [InlineData(false, "dml.rollback_outcome_unknown", "DmlRollbackOutcomeUnknown", 1L)]
    public async Task PublishedCustomMcpTool_PersistsNonretryableSqlServerUncertainOutcome(
        bool loseCommitAck, string errorCode, string auditCategory, long expectedCount)
    {
        var source = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(source))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "SQL Server 2022 Custom MCP proof requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Requires real SQL Server 2022.");
        }

        var ct = TestContext.Current.CancellationToken;
        var target = new SqlConnectionStringBuilder(source) { Pooling = false }.ConnectionString;
        var table = "mcpack_" + Guid.NewGuid().ToString("N")[..15];
        var auditPath = Path.Combine(Path.GetTempPath(),
            "sqlserver-unknown-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");
        await using var targetAdmin = new SqlConnection(target);
        for (var retry = 0; ; retry++)
        {
            try { await targetAdmin.OpenAsync(ct); break; }
            catch (SqlException) when (retry < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        try
        {
            await ExecAsync(targetAdmin, "CREATE TABLE dbo." + table
                + " (id INTEGER PRIMARY KEY, note NVARCHAR(64) NOT NULL)", ct);

            await using var audit = OpenAuditContext(auditPath);
            await audit.Database.EnsureCreatedAsync(ct);

            var context = new DefaultHttpContext
            {
                TraceIdentifier = "incoming-sqlserver-mcp-request"
            };
            context.Items[McpContextItemKeys.SqlProvider] = "MsSqlServer";
            context.Items[McpContextItemKeys.SqlConnectionString] = target;
            context.Items[McpContextItemKeys.AccessKeyId] = 7;
            context.Items[McpContextItemKeys.DbManagementId] = 42;
            context.Items[McpContextItemKeys.DatabaseName] = targetAdmin.Database;
            context.Items[McpContextItemKeys.AllowedTools] = "archive_order";
            context.Items[McpContextItemKeys.TableWhitelist] = "dbo." + table;
            var http = new Mock<IHttpContextAccessor>();
            http.SetupGet(x => x.HttpContext).Returns(context);

            var toolSql = "INSERT INTO dbo." + table + "(id,note)"
                + " VALUES(100,'sensitive_note');"
                + "INSERT INTO dbo." + table + "(id,note)"
                + " VALUES(101,'second_value')";
            var published = new Mock<ICustomSqlToolService>();
            published.Setup(x => x.GetPublishedToolByNameAsync(
                    "archive_order", 42, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CustomSqlTool
                {
                    Name = "archive_order",
                    Type = "DML",
                    SqlTemplate = toolSql
                });

            var provider = new AcknowledgementDroppingMsSqlServerProvider();
            var factory = new Mock<ISqlProviderFactory>();
            factory.Setup(x => x.GetProvider(SqlAgentToolType.MsSqlServer)).Returns(provider);

            var state = new SecurityPolicyRuntimeState();
            state.SetCurrent(new SecurityPolicyModel
            {
                DmlMaxAffectedRows = 16,
                MaxConcurrentSql = 1,
                RequireWhereForUpdate = true,
                RequireWhereForDelete = true
            });
            var approval = new CallbackApprovalProvider(async () =>
            {
                if (!loseCommitAck)
                    await ExecAsync(targetAdmin,
                        "INSERT INTO dbo." + table
                        + "(id,note) VALUES(101,'racing-row')", ct);

                // Deliberately alter only the transaction execution connection,
                // after runtime profile verification and preview have completed.
                provider.Mode = loseCommitAck
                    ? LostAcknowledgementMode.Commit
                    : LostAcknowledgementMode.Rollback;
            });
            var proxy = new CustomToolProxy(
                "archive_order", published.Object, http.Object, factory.Object,
                new AuditService(audit, http.Object),
                Mock.Of<IQueryValueParserService>(),
                state, new SqlExecutionConcurrencyLimiter(state),
                dmlApprovalProvider: approval);

            var response = await proxy.Execute(JsonSerializer.SerializeToElement(new { }),
                cancellationToken: ct);

            Assert.NotNull(approval.LastRequest);
            Assert.Contains("status=outcome_unknown", response, StringComparison.Ordinal);
            Assert.Contains("retryable=false", response, StringComparison.Ordinal);
            Assert.Contains("errorCode=" + errorCode, response, StringComparison.Ordinal);
            Assert.Contains("approvalRequestId=" + approval.LastRequest.RequestId,
                response, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive_note", response, StringComparison.Ordinal);
            Assert.DoesNotContain("second_value", response, StringComparison.Ordinal);

            // Not an in-memory audit double: re-open independent persisted
            // Audit Store and search the approval request's correlation ID.
            await using var persisted = OpenAuditContext(auditPath);
            var record = await persisted.AuditLogs.AsNoTracking().SingleAsync(
                x => x.Action == "mcp.archive_order.executed", ct);
            Assert.Equal("outcome_unknown", record.Result);
            Assert.Equal(auditCategory, record.ErrorCategory);
            Assert.Equal("approved", record.ApprovalStatus);
            Assert.Null(record.AffectedRows);
            Assert.Equal("incoming-sqlserver-mcp-request", record.RequestId);
            using var definition = JsonDocument.Parse(Assert.IsType<string>(record.Definition));
            Assert.Equal(approval.LastRequest.RequestId,
                definition.RootElement.GetProperty("ApprovalRequestId").GetString());
            var searchable = await new AuditService(
                persisted, Mock.Of<IHttpContextAccessor>())
                .QueryAsync(new AuditLogFilter
                {
                    Action = "mcp.archive_order.executed",
                    Keyword = approval.LastRequest.RequestId
                }, ct);
            Assert.Single(searchable.Items);
            Assert.Equal(record.EventId, searchable.Items.Single().EventId);

            // Independently observe the physical target through another
            // Sql connection: a lost rollback must not commit the first row.
            await using var witness = new SqlConnection(target);
            await witness.OpenAsync(ct);
            Assert.Equal(expectedCount, Convert.ToInt64(await ScalarAsync(witness,
                "SELECT count(*) FROM " + "dbo." + table, ct)));
            Assert.Equal(loseCommitAck ? 1L : 0L,
                Convert.ToInt64(await ScalarAsync(witness,
                    "SELECT count(*) FROM " + "dbo." + table + " WHERE id=100", ct)));
            Assert.Equal(loseCommitAck ? "second_value" : "racing-row",
                (await ScalarAsync(witness,
                    "SELECT note FROM " + "dbo." + table + " WHERE id=101", ct))?.ToString());
        }
        finally
        {
            await ExecAsync(targetAdmin, "DROP TABLE IF EXISTS dbo." + table,
                CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (File.Exists(auditPath)) File.Delete(auditPath);
        }
    }

    private static AdminContext OpenAuditContext(string file) =>
        new(new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = file }.ToString())
            .Options);

    private sealed class CallbackApprovalProvider(Func<Task> beforeApproval)
        : IDmlApprovalProvider
    {
        public DmlApprovalRequest? LastRequest { get; private set; }

        public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await beforeApproval();
            return DmlApprovalResult.Approve(request, "sqlserver-physical-reviewer");
        }
    }
}
