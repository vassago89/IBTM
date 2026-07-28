using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace IBTM.PcbSupply;

public sealed class PcbHandoff
{
    private readonly Channel<bool> _pcbs = Channel.CreateBounded<bool>(2);
    private readonly SemaphoreSlim _picked = new(0, 1);
    private readonly SemaphoreSlim _clear = new(0, 1);

    public ValueTask OfferAsync(
        bool pcbPresent,
        CancellationToken cancellationToken) =>
        _pcbs.Writer.WriteAsync(pcbPresent, cancellationToken);

    public ValueTask<bool> TakeAsync(CancellationToken cancellationToken) =>
        _pcbs.Reader.ReadAsync(cancellationToken);

    public Task WaitForPickupAsync(CancellationToken cancellationToken) =>
        _picked.WaitAsync(cancellationToken);

    public void ConfirmPickup() => _picked.Release();

    public Task WaitUntilClearAsync(CancellationToken cancellationToken) =>
        _clear.WaitAsync(cancellationToken);

    public void ConfirmClear() => _clear.Release();
}
