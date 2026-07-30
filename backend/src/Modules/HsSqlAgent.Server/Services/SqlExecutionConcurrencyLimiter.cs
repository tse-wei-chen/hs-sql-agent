using Admin.Service.Interfaces;

namespace HsSqlAgent.Server.Services;

public interface ISqlExecutionConcurrencyLimiter
{
    IDisposable? TryAcquire();
    int ActiveCount { get; }
}

public sealed class SqlExecutionConcurrencyLimiter(
    ISecurityPolicyRuntimeState securityPolicyRuntimeState) : ISqlExecutionConcurrencyLimiter
{
    private readonly Lock _sync = new();
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

    public IDisposable? TryAcquire()
    {
        lock (_sync)
        {
            var maximum = _securityPolicyRuntimeState.GetCurrent().MaxConcurrentSql;
            if (_activeCount >= maximum)
                return null;
            _activeCount++;
            return new Lease(this);
        }
    }

    private void Release()
    {
        lock (_sync)
            _activeCount--;
    }

    private sealed class Lease(SqlExecutionConcurrencyLimiter owner) : IDisposable
    {
        private SqlExecutionConcurrencyLimiter? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
