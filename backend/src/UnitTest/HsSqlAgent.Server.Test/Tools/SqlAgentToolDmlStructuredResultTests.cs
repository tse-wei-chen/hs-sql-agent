using Admin.Service.Interfaces;
using Common.Models;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class SqlAgentToolDmlStructuredResultTests
{
    [Fact]
    public void CommittedResult_PreservesReturnedRowsAsStructuredContent()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?>
            {
                ["id"] = 7,
                ["status"] = "updated"
            }
        ];

        var result = McpDmlToolResult.CommittedResult(
            "Postgres",
            1,
            1,
            12,
            "request-1",
            rows,
            "Committed.");

        Assert.Equal("committed", result.Status);
        Assert.True(result.Committed);
        Assert.Equal("approved", result.ApprovalDecision);
        Assert.Equal(1, result.AffectedRows);
        Assert.Null(result.Error);
        var row = Assert.Single(result.ReturnedRows);
        Assert.Equal(7, row["id"]);
        Assert.Equal("updated", row["status"]);
    }

    [Fact]
    public void OutcomeUnknown_UsesDistinctNonRetryableMcpStatus_AndNoClaimOfRollback()
    {
        var result = McpDmlToolResult.OutcomeUnknown(
            "Postgres",
            2,
            37,
            "approval-123",
            "COMMIT acknowledgement lost; reconcile database state before new preview.");

        Assert.Equal("outcome_unknown", result.Status);
        Assert.False(result.Committed); // Legacy bit means unconfirmed, not rolled back.
        Assert.Null(result.CommitConfirmed);
        Assert.Null(result.AffectedRows); // The preview count is not a committed count.
        Assert.Equal("approved", result.ApprovalDecision);
        Assert.Equal("approval-123", result.ApprovalRequestId);
        Assert.Empty(result.ReturnedRows);
        Assert.NotNull(result.Error);
        Assert.Equal("dml.commit_outcome_unknown", result.Error.Code);
        Assert.Equal("Commit", result.Error.Stage);
        Assert.False(result.Error.Retryable);

        var serialized = System.Text.Json.JsonSerializer.Serialize(
            result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"status\":\"outcome_unknown\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"commitConfirmed\":null", serialized, StringComparison.Ordinal);
        Assert.Contains("\"retryable\":false", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeUnknown_OriginalFiveArgumentClrSignatureStillExists()
    {
        var method = typeof(McpDmlToolResult).GetMethod(
            nameof(McpDmlToolResult.OutcomeUnknown),
            [typeof(string), typeof(int), typeof(long), typeof(string), typeof(string)]);
        Assert.NotNull(method);
        Assert.True(method.IsPublic);
        Assert.True(method.IsStatic);
    }

    [Fact]
    public void RollbackOutcomeUnknown_IsNonRetryableAndNotReportedAsFailure()
    {
        var result = McpDmlToolResult.OutcomeUnknown(
            "Postgres", 1, 12, "approval-rollback",
            "ROLLBACK unconfirmed; reconcile the target before a new preview.",
            "dml.rollback_outcome_unknown");

        Assert.Equal("outcome_unknown", result.Status);
        Assert.False(result.Committed);
        Assert.Null(result.CommitConfirmed);
        Assert.Null(result.AffectedRows);
        Assert.Equal("approved", result.ApprovalDecision);
        Assert.Equal("approval-rollback", result.ApprovalRequestId);
        Assert.Equal("dml.rollback_outcome_unknown", result.Error?.Code);
        Assert.Equal("Rollback", result.Error?.Stage);
        Assert.False(result.Error!.Retryable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedDmlCommit_AuditFailureOrCancellation_NeverTurnsCommitIntoFailed(bool cancelled)
    {
        var auditAttempted = false;
        var execution = new TypedDmlExecutionTiming(
            "Successfully committed.",
            ApprovalWaitDurationMs: 18,
            AffectedRows: 2,
            Committed: true,
            ApprovalRequestId: "approval-already-consumed",
            ReturnedRows: [new Dictionary<string, object?> { ["ID"] = 42 }]);

        var result = await SqlAgentTool.CompleteConfirmedDmlAsync(
            "Sqlite", 2, execution, () =>
            {
                auditAttempted = true;
                return cancelled
                    ? Task.FromException(new OperationCanceledException("Audit cancellation after target COMMIT."))
                    : Task.FromException(new IOException("Admin audit database unavailable after target COMMIT."));
            });

        Assert.True(auditAttempted);
        Assert.Equal("committed", result.Status);
        Assert.True(result.Committed);
        Assert.Equal(true, result.CommitConfirmed);
        Assert.Equal("approved", result.ApprovalDecision);
        Assert.Equal("approval-already-consumed", result.ApprovalRequestId);
        Assert.Equal(2, result.AffectedRows);
        Assert.Equal(42, Assert.Single(result.ReturnedRows)["ID"]);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ConfirmedDmlCommit_HappyAudit_StillReturnsSameCommittedResult()
    {
        var calls = 0;
        var execution = new TypedDmlExecutionTiming(
            "Committed.", 0, 1, true, ApprovalRequestId: "approval-once");
        var result = await SqlAgentTool.CompleteConfirmedDmlAsync(
            "Postgres", 1, execution, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        Assert.Equal("committed", result.Status);
        Assert.True(result.Committed);
        Assert.Equal("approval-once", result.ApprovalRequestId);
    }

    [Fact]
    public async Task ConfirmedDmlCommit_RejectsUnconfirmedExecution()
    {
        var auditCalled = false;
        var execution = new TypedDmlExecutionTiming("Not committed.", 0, null, false);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SqlAgentTool.CompleteConfirmedDmlAsync(
                "Postgres", 1, execution, () => { auditCalled = true; return Task.CompletedTask; }));
        Assert.False(auditCalled);
    }

    [Fact]
    public async Task ExecuteDmlSql_InvalidConfiguration_ReturnsStructuredFailureWithoutSecret()
    {
        const string secretConnectionString =
            "Host=prod.example;Database=payments;Username=service;Password=super-secret";
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[McpContextItemKeys.SqlProvider] = "NotAProvider";
        context.Items[McpContextItemKeys.SqlConnectionString] = secretConnectionString;
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            Mock.Of<ISqlProviderFactory>(),
            Mock.Of<IAuditService>(),
            Mock.Of<IDbSemanticService>(),
            Mock.Of<ISecurityPolicyRuntimeState>(),
            Mock.Of<ISqlExecutionConcurrencyLimiter>());

        var result = await tool.ExecuteDmlSql(
            "UPDATE public.users SET name = 'Alice' WHERE id = 7",
            null!,
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.False(result.Committed);
        Assert.Equal(0, result.StatementCount);
        Assert.Null(result.Provider);
        Assert.Empty(result.ReturnedRows);
        Assert.NotNull(result.Error);
        Assert.Equal("configuration.invalid", result.Error.Code);
        Assert.Equal("Configuration", result.Error.Stage);
        Assert.Equal("Invalid database provider or connection configuration.", result.Error.Message);
        Assert.DoesNotContain("super-secret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretConnectionString, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretConnectionString, result.Error.Message, StringComparison.Ordinal);
    }
}
