using HsSqlAgent.Server.Tools;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed class CustomToolDmlOutcomeBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedCommit_AuditFailureOrCancellation_NeverReportsRetryableFailure(bool cancelled)
    {
        var calls = 0;
        var execution = new TypedDmlExecutionTiming(
            "Success | statements=2 | affectedRows=2 | committed",
            15, 2, true, ApprovalRequestId: "approval-consumed");

        var result = await CustomToolProxy.CompleteTerminalDmlOutcomeAsync(
            execution,
            () =>
            {
                calls++;
                return cancelled
                    ? Task.FromException(new OperationCanceledException("Audit cancelled after COMMIT."))
                    : Task.FromException(new IOException("Audit storage failed after COMMIT."));
            });

        Assert.Equal(1, calls);
        Assert.Equal(execution.Result, result);
        Assert.DoesNotContain("try again", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Error:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmedCommit_SuccessfulAudit_PreservesOriginalResult()
    {
        var calls = 0;
        var execution = new TypedDmlExecutionTiming("Success | committed", 0, 1, true);
        var result = await CustomToolProxy.CompleteTerminalDmlOutcomeAsync(
            execution, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        Assert.Equal(execution.Result, result);
    }

    [Theory]
    [InlineData("dml.commit_outcome_unknown", false)]
    [InlineData("dml.commit_outcome_unknown", true)]
    [InlineData("dml.rollback_outcome_unknown", false)]
    [InlineData("dml.rollback_outcome_unknown", true)]
    public async Task UnknownPhysicalOutcome_AuditFailureOrCancellation_PreservesNonRetryableWarning(
        string errorCode, bool cancelled)
    {
        var calls = 0;
        var execution = new TypedDmlExecutionTiming(
            "Driver exception: Password=internal-secret",
            21, null, false,
            ApprovalDecision: HsSqlAgent.Approvals.DmlApprovalDecision.Approved,
            ApprovalRequestId: "approval-for-reconciliation",
            ErrorCode: errorCode, ErrorStage: "Commit", Retryable: false);

        var result = await CustomToolProxy.CompleteTerminalDmlOutcomeAsync(
            execution,
            () =>
            {
                calls++;
                return cancelled
                    ? Task.FromException(new OperationCanceledException("Audit was cancelled."))
                    : Task.FromException(new IOException("Audit unavailable."));
            });

        Assert.Equal(1, calls);
        Assert.Contains("status=outcome_unknown", result);
        Assert.Contains(errorCode, result);
        Assert.Contains("retryable=false", result);
        Assert.Contains("approval-for-reconciliation", result);
        Assert.Contains("reconcile", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("execute_dml_sql", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal-secret", result);
        Assert.DoesNotContain("try again", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonterminalDmlOutcome_CannotUseTerminalResultBoundary()
    {
        var auditCalled = false;
        var execution = new TypedDmlExecutionTiming("pending", 0, null, false);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CustomToolProxy.CompleteTerminalDmlOutcomeAsync(
                execution, () => { auditCalled = true; return Task.CompletedTask; }));
        Assert.False(auditCalled);
    }
}
