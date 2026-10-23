using System.Data.Common;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Commit was attempted, but its acknowledgement was not received. The database
/// might have committed successfully. The consumed approval must never be retried;
/// callers must reconcile the physical database state before a new preview.
/// </summary>
public sealed class DmlCommitOutcomeUnknownException : InvalidOperationException
{
    public DmlCommitOutcomeUnknownException(Exception cause)
        : base(
            "DML commit outcome is unknown: the database may already have committed. " +
            "The one-time approval was consumed and must not be replayed. " +
            "Verify the database state before starting a new preview.",
            cause)
    {
    }
}

/// <summary>
/// A DML attempt failed before any acknowledged COMMIT, and a subsequent
/// ROLLBACK could not be confirmed. It would be unsafe to report a definite
/// failure or allow the consumed approval to be replayed.
/// </summary>
public sealed class DmlRollbackOutcomeUnknownException : InvalidOperationException
{
    public DmlRollbackOutcomeUnknownException(Exception originalFailure, Exception rollbackFailure)
        : base(
            "DML rollback outcome is unknown: the database transaction could not be confirmed rolled back. " +
            "The one-time approval was consumed and must not be replayed. " +
            "Investigate physical database effects before starting a new preview.",
            originalFailure)
    {
        RollbackFailure = rollbackFailure;
    }

    public Exception RollbackFailure { get; }
}

/// <summary>
/// Shared failure handling for single and atomic DML. A failed COMMIT must never
/// be misreported as a definitive rollback; rollback errors must not replace
/// the original SQL/cancellation/commit failure.
/// </summary>
internal static class DmlTransactionFailureRecovery
{
    internal const string RollbackFailureDataKey = "HsSqlAgent.DmlRollbackFailure";
    private static readonly TimeSpan RollbackCleanupTimeout = TimeSpan.FromSeconds(10);

    internal static async Task CommitAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception cause)
        {
            // Even if the provider reports a timeout, failed acknowledgement
            // does not prove whether COMMIT reached the database.
            throw new DmlCommitOutcomeUnknownException(cause);
        }
    }

    // Invoke only after rollback cleanup. Preserve the original exception
    // and stack on a clean rollback; on a failed rollback surface a separate
    // typed terminal uncertainty, except when COMMIT was already uncertain.
    internal static void ThrowIfRollbackUnconfirmed(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is DmlCommitOutcomeUnknownException)
            return;
        if (failure.Data[RollbackFailureDataKey] is Exception rollbackFailure)
            throw new DmlRollbackOutcomeUnknownException(failure, rollbackFailure);
    }

    internal static async Task RollbackWithoutMaskingAsync(
        DbTransaction transaction,
        Exception originalFailure)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(originalFailure);

        try
        {
            // The request token may already be cancelled (or cancelled after
            // COMMIT). Cleanup needs its own bounded window, not that token.
            using var cleanup = new CancellationTokenSource(RollbackCleanupTimeout);
            await transaction.RollbackAsync(cleanup.Token);
        }
        catch (Exception rollbackFailure)
        {
            // Preserve the actual failure and its original stack trace.
            // Diagnostics retain rollback failure without making a
            // potentially-committed transaction appear safely rolled back.
            originalFailure.Data[RollbackFailureDataKey] = rollbackFailure;
        }
    }
}
