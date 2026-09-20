using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgCarrierConveyor
{
    public async Task WaitForRepeatEndAsync(CancellationToken cancellationToken)
    {
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        try
        {
            while (State != NgConveyorState.ReadyToEject || RunCommandOn)
                await changed.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= changed.Set;
        }
    }

    public async Task ReturnToShuttleAsync(CancellationToken cancellationToken)
    {
        if (CarrierCount != 1)
            throw new InvalidOperationException("NG return requires one carrier with known presence feedback.");
        if (_shuttle.Lift != NgShuttleLiftState.Down && !Position3Occupied)
            throw new InvalidOperationException("Lower the NG shuttle before returning the carrier.");

        _movement = Movement.None;
        _ejectionPhase = EjectionPhase.Idle;
        await SetStopperDownAsync(true, cancellationToken);
        await RunUntilAsync(InputIo.NgShuttleCarrierDetected, true, true, cancellationToken);
    }
}
