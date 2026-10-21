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
