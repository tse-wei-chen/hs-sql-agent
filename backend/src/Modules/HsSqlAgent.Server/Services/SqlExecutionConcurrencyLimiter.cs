using Admin.Service.Interfaces;

namespace HsSqlAgent.Server.Services;

public interface ISqlExecutionConcurrencyLimiter
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(
        CancellationToken cancellationToken = default);
}

public sealed class SqlExecutionConcurrencyLimiter(
    ISecurityPolicyRuntimeState securityPolicyRuntimeState) : ISqlExecutionConcurrencyLimiter
{
#if NET9_0_OR_GREATER
    private readonly Lock _sync = new();
#else
    private readonly object _sync = new();
#endif
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private int _activeCount;

    public int ActiveCount
    {
        get
        {
            lock (_sync)
                return _activeCount;
        }
    }

    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var maximum = _securityPolicyRuntimeState.GetCurrent().MaxConcurrentSql;
            if (_activeCount >= maximum)
                return ValueTask.FromResult<IAsyncDisposable?>(null);
            _activeCount++;
            return ValueTask.FromResult<IAsyncDisposable?>(new Lease(this));
        }
    }

    private void Release()
    {
        lock (_sync)
            _activeCount--;
    }

    private sealed class Lease(SqlExecutionConcurrencyLimiter owner) : IAsyncDisposable
    {
        private SqlExecutionConcurrencyLimiter? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
