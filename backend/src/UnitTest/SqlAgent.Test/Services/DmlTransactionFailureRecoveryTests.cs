using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task RealSqliteCommitSucceedsButAcknowledgementLost_RemainsUnknownAndDoesNotUndoWrite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE approvals (id INTEGER PRIMARY KEY)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var committedTransaction = await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)committedTransaction;
            insert.CommandText = "INSERT INTO approvals (id) VALUES (1)";
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // This decorator forwards COMMIT to a real SQLite transaction and
        // injects a lost provider acknowledgement only AFTER it succeeds.
        var uncertain = new CommitThenDropAcknowledgementTransaction(committedTransaction);
        var error = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(() =>
            DmlTransactionFailureRecovery.CommitAsync(
                uncertain, TestContext.Current.CancellationToken));

        Assert.IsType<IOException>(error.InnerException);
        await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(uncertain, error);
        // SQLite already committed, so rollback is no longer possible.
        Assert.NotNull(error.Data[DmlTransactionFailureRecovery.RollbackFailureDataKey]);

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM approvals";
        Assert.Equal(1L,
            (long)(await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        Assert.Contains("outcome is unknown", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CommitThenDropAcknowledgementTransaction(DbTransaction inner)
        : DbTransaction
    {
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection DbConnection => inner.Connection!;

        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();

        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken);
            throw new IOException("SQLite committed, but network acknowledgement was lost.");
        }

        public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);
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
