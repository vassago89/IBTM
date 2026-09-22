using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgCarrierConveyor
{
    // Pending ownership is cleared by the release operation, never by presence DI.
    private bool IsTransferClear => _transfer.IsClear
        && _io.GetInput(InputIo.NgCarrierGripperOpen)
        && !_io.GetInput(InputIo.NgCarrierGripperClosed);

    public bool IsReceiveAllowed(bool useConveyor, bool? conveyorRunning = null)
    {
        return ShuttleLift == NgShuttleLiftState.Up
            && !Position3Occupied
            && (!useConveyor || IsAcceptCarrierAllowed(conveyorRunning));
    }

    public Task SetShuttleDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTransferClear)
            throw new MotionInterlockException("Complete the NG transfer release and raise the open pickup before moving the shuttle.");
        return _io.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, down, cancellationToken);
    }

    public async Task WaitForCarrierAsync(CancellationToken cancellationToken = default)
    {
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!Position3Occupied || !IsTransferClear)
                await changed.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= changed.Set;
        }
    }

}
