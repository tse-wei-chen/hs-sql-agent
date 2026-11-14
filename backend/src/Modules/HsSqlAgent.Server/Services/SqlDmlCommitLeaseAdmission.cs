namespace HsSqlAgent.Server.Services;

/// <summary>
/// Checks admission before starting a DML commit. The revocation signal is
/// deliberately NOT linked to a COMMIT already in flight: cancellation after
/// native COMMIT begins cannot establish whether the database committed.
/// </summary>
internal static class SqlDmlCommitLeaseAdmission
{
    internal const string ErrorCode = "dml.lease_revoked_before_commit";

    internal const string RefusalMessage =
        "Distributed SQL admission expired before the DML commit was started. " +
        "No database COMMIT was attempted; request a fresh preview and approval. " +
        "Do not replay the previous approval.";

    internal static bool IsRevoked(IAsyncDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease is IRevocableSqlExecutionLease { RevocationToken.IsCancellationRequested: true };
    }

    internal static void ThrowIfRevoked(IAsyncDisposable lease)
    {
        if (IsRevoked(lease))
            throw new InvalidOperationException(RefusalMessage);
    }
}
