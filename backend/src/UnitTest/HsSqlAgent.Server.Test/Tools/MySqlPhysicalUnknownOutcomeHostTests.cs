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
using MySql.Data.MySqlClient;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Real MySQL 16 target + independent persistent SQLite Admin Audit.
/// The published Custom MCP proxy, approval, compiler and executor are real.
/// </summary>
public sealed partial class MySqlPhysicalUnknownOutcomeIntegrationTests
{
    [Theory]
    [InlineData(true, "dml.commit_outcome_unknown", "DmlCommitOutcomeUnknown", 2L)]
    [InlineData(false, "dml.rollback_outcome_unknown", "DmlRollbackOutcomeUnknown", 1L)]
    public async Task PublishedCustomMcpTool_PersistsNonretryableMySqlUncertainOutcome(
        bool loseCommitAck, string errorCode, string auditCategory, long expectedCount)
    {
        var source = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(source))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "MySQL 16 Custom MCP proof requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("Requires real MySQL 16.");
        }

        var ct = TestContext.Current.CancellationToken;
        var schema = "mcpack_" + Guid.NewGuid().ToString("N")[..15];
        var auditPath = Path.Combine(Path.GetTempPath(),
            "mysql-unknown-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");
        await using var serverAdmin = new MySqlConnection(source);
        await serverAdmin.OpenAsync(ct);
        await ExecAsync(serverAdmin, "CREATE DATABASE " + schema, ct);
        var target = new MySqlConnectionStringBuilder(source)
        {
            Database = schema,
            Pooling = false
        }.ConnectionString;
        try
        {
            await using var targetAdmin = new MySqlConnection(target);
            await targetAdmin.OpenAsync(ct);
            await ExecAsync(targetAdmin, "CREATE TABLE " + schema
                + ".items (id INTEGER PRIMARY KEY, note VARCHAR(64) NOT NULL) ENGINE=InnoDB", ct);

            await using var audit = OpenAuditContext(auditPath);
            await audit.Database.EnsureCreatedAsync(ct);

            var context = new DefaultHttpContext
            {
                TraceIdentifier = "incoming-postgres-mcp-request"
            };
            context.Items[McpContextItemKeys.SqlProvider] = "MySql";
            context.Items[McpContextItemKeys.SqlConnectionString] = target;
            context.Items[McpContextItemKeys.AccessKeyId] = 7;
            context.Items[McpContextItemKeys.DbManagementId] = 42;
            context.Items[McpContextItemKeys.DatabaseName] = targetAdmin.Database;
            context.Items[McpContextItemKeys.AllowedTools] = "archive_order";
            context.Items[McpContextItemKeys.TableWhitelist] = schema + ".items";
            var http = new Mock<IHttpContextAccessor>();
            http.SetupGet(x => x.HttpContext).Returns(context);

            var toolSql = "INSERT INTO " + schema + ".items(id,note)"
                + " VALUES(100,'sensitive_note');"
                + "INSERT INTO " + schema + ".items(id,note)"
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

            var provider = new AcknowledgementDroppingMySqlProvider();
            var factory = new Mock<ISqlProviderFactory>();
            factory.Setup(x => x.GetProvider(SqlAgentToolType.MySQL)).Returns(provider);

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
                        "INSERT INTO " + schema
                        + ".items(id,note) VALUES(101,'racing-row')", ct);

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
            Assert.Equal("incoming-postgres-mcp-request", record.RequestId);
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
            // MySql connection: a lost rollback must not commit the first row.
            await using var witness = new MySqlConnection(target);
            await witness.OpenAsync(ct);
            Assert.Equal(expectedCount, Convert.ToInt64(await ScalarAsync(witness,
                "SELECT count(*) FROM " + schema + ".items", ct)));
            Assert.Equal(loseCommitAck ? 1L : 0L,
                Convert.ToInt64(await ScalarAsync(witness,
                    "SELECT count(*) FROM " + schema + ".items WHERE id=100", ct)));
            Assert.Equal(loseCommitAck ? "second_value" : "racing-row",
                (await ScalarAsync(witness,
                    "SELECT note FROM " + schema + ".items WHERE id=101", ct))?.ToString());
        }
        finally
        {
            await ExecAsync(serverAdmin, "DROP DATABASE IF EXISTS " + schema,
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
            return DmlApprovalResult.Approve(request, "postgres-physical-reviewer");
        }
    }
}
