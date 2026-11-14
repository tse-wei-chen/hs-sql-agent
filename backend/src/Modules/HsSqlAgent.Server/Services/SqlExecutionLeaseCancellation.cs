namespace HsSqlAgent.Server.Services;

/// <summary>
/// An admitted SQL operation that can lose its distributed Redis lease.
/// This opt-in interface preserves the existing public limiter contract:
/// the in-process / legacy leases remain ordinary IAsyncDisposable instances.
/// </summary>
internal interface IRevocableSqlExecutionLease : IAsyncDisposable
{
    CancellationToken RevocationToken { get; }
}

/// <summary>
/// Connects caller cancellation with Redis admission revocation for a running
/// read-only SQL operation. Never treat a lease as a reliable execution fence
/// for an already-started DML COMMIT with an ambiguous outcome.
/// </summary>
internal sealed class SqlExecutionLeaseCancellation : IDisposable
{
    private readonly CancellationTokenSource? _linked;
    public CancellationToken Token { get; }

    public SqlExecutionLeaseCancellation(
        IAsyncDisposable lease,
        CancellationToken requestCancellation)
    {
        if (lease is IRevocableSqlExecutionLease revocable)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellation, revocable.RevocationToken);
            Token = _linked.Token;
        }
        else
        {
            // Existing memory/third-party leases keep the same caller token,
            // including its identity, for binary and behavioral compatibility.
            Token = requestCancellation;
        }
    }

    public void Dispose() => _linked?.Dispose();
}
