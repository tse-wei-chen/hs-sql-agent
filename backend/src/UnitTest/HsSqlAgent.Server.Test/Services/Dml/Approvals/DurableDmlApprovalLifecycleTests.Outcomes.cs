using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed partial class DurableDmlApprovalLifecycleTests
{
    [Fact]
    public async Task AuditFailureAfterSuccessfulDatabaseCommit_DoesNotRewriteExecutedAsFailed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(failAudit: true))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Executed, completion.Status);
            Assert.Equal(1, completion.AffectedRows);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var persisted = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            Assert.Equal("Executed", persisted.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var services = fixture.CreateRestartedServices())
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, replay.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetCommittedButAdminStateSaveFails_NeverReturnsFailedOrReexecutes(
        bool fallbackStateSaveAlsoFails)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(
            failTerminalSave: true,
            failUnknownSave: fallbackStateSaveAlsoFails))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, result.Status);
            Assert.Equal("dml.approval_state_unknown", result.ErrorCode);
            Assert.False(result.Retryable);
            Assert.Equal(1, result.AffectedRows);
            Assert.Contains("COMMIT succeeded", result.Message, StringComparison.Ordinal);
            Assert.Contains("do not retry", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            Assert.Equal(
                fallbackStateSaveAlsoFails ? "Executing" : "ApprovalStateUnknown",
                state.Status);
            Assert.NotEqual("Failed", state.Status);

            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var retry = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                fallbackStateSaveAlsoFails
                    ? DmlApprovalCompletionStatus.AlreadyProcessing
                    : DmlApprovalCompletionStatus.OutcomeUnknown,
                retry.Status);
            Assert.False(retry.Retryable);
            Assert.Contains("Do not retry", retry.Message, StringComparison.OrdinalIgnoreCase);
            if (!fallbackStateSaveAlsoFails)
                Assert.Equal("dml.approval_state_unknown", retry.ErrorCode);
        }

        // A fresh service instance must not execute the approval again.
        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task AdminTerminalSaveAcknowledgedByDatabaseButResponseLost_RemainsNonReplayable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(
            failTerminalSave: true,
            failTerminalSaveAfterSuccess: true))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            // A missing Admin Store acknowledgement does not change the known
            // outcome of the separate target SQL transaction.
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, result.Status);
            Assert.Equal("dml.approval_state_unknown", result.ErrorCode);
            Assert.False(result.Retryable);
            Assert.Contains("COMMIT succeeded", result.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var stored = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            // The first terminal write completed. The fallback might have
            // conservatively replaced it with ApprovalStateUnknown; neither permits
            // a second execution.
            Assert.Contains(stored.Status, new[] { "Executed", "ApprovalStateUnknown" });

            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var repeat = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Contains(repeat.Status, new[]
            {
                DmlApprovalCompletionStatus.AlreadyCompleted,
                DmlApprovalCompletionStatus.OutcomeUnknown
            });
            if (stored.Status == "ApprovalStateUnknown")
                Assert.Equal("dml.approval_state_unknown", repeat.ErrorCode);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task LegacyApprovalStateUnknown_RemainsClassifiedWithoutReExecuting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "OutcomeUnknown";
            state.Reason =
                "Target database COMMIT succeeded, but the Admin Store completion status could not be confirmed. " +
                "The approval is already consumed. Reconcile database and approval state; do not retry this approval.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var repeat = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, repeat.Status);
            Assert.Equal("dml.approval_state_unknown", repeat.ErrorCode);
            Assert.False(repeat.Retryable);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task ApprovalStateUnknown_ClassificationDoesNotDependOnReasonText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "ApprovalStateUnknown";
            state.Reason = "Operator-supplied annotation, not a classification string.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, replay.Status);
            Assert.Equal("dml.approval_state_unknown", replay.ErrorCode);
            Assert.False(replay.Retryable);
            Assert.Contains("COMMIT succeeded", replay.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PreviouslyUnconfirmedRollback_RemainsDistinctAndNonReplayableAfterRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "RollbackOutcomeUnknown";
            state.Reason = "Mutation failed and ROLLBACK acknowledgement was lost.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, replay.Status);
            Assert.Equal("dml.rollback_outcome_unknown", replay.ErrorCode);
            Assert.False(replay.Retryable);
            Assert.Contains("could not be confirmed", replay.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Do not retry", replay.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PreviouslyUnknownCommitOutcome_RemainsTerminalWithoutReExecutingApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "OutcomeUnknown";
            state.Reason = "COMMIT acknowledgement lost; physical state requires reconciliation.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var retry = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, retry.Status);
            Assert.Equal("dml.commit_outcome_unknown", retry.ErrorCode);
            Assert.False(retry.Retryable);
            Assert.Contains("Do not retry", retry.Message, StringComparison.OrdinalIgnoreCase);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            Assert.Equal("OutcomeUnknown", state.Status);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PendingApproval_AccessKeyRevokedBeforeCompletion_BecomesStaleWithoutCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var key = await context.McpAccessKeys.SingleAsync(
                x => x.Id == AccessKeyId,
                TestContext.Current.CancellationToken);
            key.IsActive = false;
            key.RevokedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    request.RequestId,
                    request.ApprovalFingerprint,
                    approverIdentity: "reviewer@example.test"),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Stale, completion.Status);
            Assert.Contains("access key", completion.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PendingApproval_WrongApprovalFingerprint_IsRejectedWithoutClaimOrCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, new string('0', 64)),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.InvalidApproval, completion.Status);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            Assert.Equal("Pending", state.Status);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

}
