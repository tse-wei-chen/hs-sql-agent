using System.Data;
using System.Data.Common;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlTransactionFailureRecoveryTests
{
    [Fact]
    public async Task CommitLostAcknowledgement_ReportsUnknownOutcomeAndRetainsOriginalDatabaseFailure()
    {
        var transportFailure = new IOException("Database accepted COMMIT but the connection dropped.");
        var rollbackFailure = new InvalidOperationException("Transaction already completed.");
        var transaction = new FailingTransaction(transportFailure, rollbackFailure);

        var result = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(() =>
            DmlTransactionFailureRecovery.CommitAsync(
                transaction, TestContext.Current.CancellationToken));

        Assert.Same(transportFailure, result.InnerException);
        Assert.Contains("outcome is unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not be replayed", result.Message, StringComparison.OrdinalIgnoreCase);

        await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(transaction, result);
        Assert.Same(rollbackFailure,
            result.Data[DmlTransactionFailureRecovery.RollbackFailureDataKey]);
        Assert.Equal(1, transaction.RollbackAttempts);
        Assert.True(transaction.RollbackToken.CanBeCanceled);
        Assert.False(transaction.RollbackToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CleanupAfterCancelledRequest_UsesIndependentCancellationToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cause = new OperationCanceledException(cancelled.Token);
        var transaction = new FailingTransaction(cause, rollbackFailure: null);

        var error = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(() =>
            DmlTransactionFailureRecovery.CommitAsync(transaction, cancelled.Token));

        await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(transaction, error);
        Assert.Same(cause, error.InnerException);
        Assert.Equal(1, transaction.RollbackAttempts);
        Assert.NotEqual(cancelled.Token, transaction.RollbackToken);
        Assert.False(transaction.RollbackToken.IsCancellationRequested);
        Assert.Null(error.Data[DmlTransactionFailureRecovery.RollbackFailureDataKey]);
    }

    [Fact]
    public async Task CleanCommit_DoesNotCreateUncertainOutcomeOrRollback()
    {
        var transaction = new FailingTransaction(null, null);

        await DmlTransactionFailureRecovery.CommitAsync(
            transaction, TestContext.Current.CancellationToken);

        Assert.Equal(1, transaction.CommitAttempts);
        Assert.Equal(0, transaction.RollbackAttempts);
    }

    [Fact]
    public async Task FailedMutationAndFailedRollback_PreservesTheMutationFailure()
    {
        var primary = new IOException("Original DB mutation error.");
        var rollback = new InvalidOperationException("Rollback failed.");
        var transaction = new FailingTransaction(null, rollback);

        await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(transaction, primary);

        Assert.Equal("Original DB mutation error.", primary.Message);
        Assert.Same(rollback,
            primary.Data[DmlTransactionFailureRecovery.RollbackFailureDataKey]);
        Assert.Equal(1, transaction.RollbackAttempts);
    }

    private sealed class FailingTransaction(
        Exception? commitFailure,
        Exception? rollbackFailure) : DbTransaction
    {
        public int CommitAttempts { get; private set; }
        public int RollbackAttempts { get; private set; }
        public CancellationToken RollbackToken { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection =>
            throw new NotSupportedException("Only transaction control is tested.");

        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();

        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitAttempts++;
            return commitFailure is null ? Task.CompletedTask : Task.FromException(commitFailure);
        }

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackAttempts++;
            RollbackToken = cancellationToken;
            return rollbackFailure is null ? Task.CompletedTask : Task.FromException(rollbackFailure);
        }
    }
}
