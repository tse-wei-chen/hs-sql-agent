namespace Admin.Service.Interfaces;

public interface IOutboundDeliverySignal
{
    void Notify();
    ValueTask<bool> WaitAsync(CancellationToken cancellationToken);
    bool TryRead();
}
