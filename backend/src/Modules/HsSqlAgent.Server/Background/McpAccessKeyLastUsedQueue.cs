using System.Threading.Channels;

namespace HsSqlAgent.Server.Background;

public class McpAccessKeyLastUsedQueue : IMcpAccessKeyLastUsedQueue
{
    private readonly Channel<int> _channel = Channel.CreateBounded<int>(new BoundedChannelOptions(10000)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public bool TryEnqueue(int keyId)
    {
        return _channel.Writer.TryWrite(keyId);
    }

    public IAsyncEnumerable<int> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
