using System.Threading.Channels;
using Admin.Service.Interfaces;

namespace Admin.Service.Services;

public class OutboundDeliverySignal : IOutboundDeliverySignal
{
    private readonly Channel<byte> _channel = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    public void Notify() => _channel.Writer.TryWrite(0);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public bool TryRead() => _channel.Reader.TryRead(out _);
}
