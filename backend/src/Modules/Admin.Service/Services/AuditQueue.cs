using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using System.Threading.Channels;

namespace Admin.Service.Services;

public class AuditQueue : IAuditQueue
{
    private readonly Channel<AuditLog> _channel = Channel.CreateBounded<AuditLog>(new BoundedChannelOptions(50000)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public bool TryEnqueue(AuditLog log)
    {
        return _channel.Writer.TryWrite(log);
    }

    public IAsyncEnumerable<AuditLog> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
