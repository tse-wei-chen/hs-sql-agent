namespace HsSqlAgent.Server.Background;

public interface IMcpAccessKeyLastUsedQueue
{
    bool TryEnqueue(int keyId);
    IAsyncEnumerable<int> DequeueAllAsync(CancellationToken cancellationToken = default);
}
