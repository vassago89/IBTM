using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Transport;

public sealed class CarrierJigPositioner
{
    private readonly IIoService _io;
    private readonly int _presentInputChannel;
    private readonly int _stopperUpOutputChannel;
    private readonly int _backupPlateUpOutputChannel;

    public CarrierJigPositioner(
        IIoService io,
        int presentInputChannel,
        int stopperUpOutputChannel,
        int backupPlateUpOutputChannel)
    {
        _io = io;
        _presentInputChannel = presentInputChannel;
        _stopperUpOutputChannel = stopperUpOutputChannel;
        _backupPlateUpOutputChannel = backupPlateUpOutputChannel;
    }

    public void Initialize()
    {
        _io.SetOutput(_stopperUpOutputChannel, false);
        _io.SetOutput(_backupPlateUpOutputChannel, false);
    }

    public async Task PositionAsync(CancellationToken cancellationToken)
    {
        await WaitUntilPresentAsync(cancellationToken);
        _io.SetOutput(_backupPlateUpOutputChannel, true);
    }

    public Task WaitUntilPresentAsync(CancellationToken cancellationToken) =>
        _io.WaitForInputAsync(_presentInputChannel, true, cancellationToken);

    public Task WaitUntilEmptyAsync(CancellationToken cancellationToken) =>
        _io.WaitForInputAsync(_presentInputChannel, false, cancellationToken);

    public async Task CompleteRemovalAsync(CancellationToken cancellationToken)
    {
        await WaitUntilEmptyAsync(cancellationToken);
        _io.SetOutput(_backupPlateUpOutputChannel, false);
        _io.SetOutput(_stopperUpOutputChannel, false);
    }

    internal async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        _io.SetOutput(_backupPlateUpOutputChannel, false);
        _io.SetOutput(_stopperUpOutputChannel, true);
        await WaitUntilEmptyAsync(cancellationToken);
        _io.SetOutput(_stopperUpOutputChannel, false);
    }
}
