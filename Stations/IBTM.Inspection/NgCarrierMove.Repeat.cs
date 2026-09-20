using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed partial class NgCarrierMove
{
    public async Task WaitForRepeatEndAsync(bool holdAtShuttle, CancellationToken cancellationToken)
    {
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        try
        {
            var endState = holdAtShuttle ? NgTransferState.HoldingAtDestination : NgTransferState.Completed;
            while (GetState(NgTransferDestination.Shuttle,
                canPickUp: true, holdAtDestination: holdAtShuttle,
                allowEmpty: IsEmptyRepeatAllowed) != endState)
                await changed.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= changed.Set;
        }
    }

    public async Task ReturnToStationAsync(CancellationToken cancellationToken)
    {
        await _work.Station.SeatAsync(cancellationToken);
        await RunToAsync(NgTransferDestination.Station, cancellationToken,
            allowEmpty: IsEmptyRepeatAllowed);
    }

    public async Task ClearStationAsync(AxisPosition? firstFov, CancellationToken cancellationToken)
    {
        if (firstFov is null)
            return;
        if ((!IsEmptyRepeatAllowed && !_work.Station.CarrierPresent)
            || _pickup.Gripper != NgTransferGripperState.Open
            || !_pickup.IsRaised)
            throw new InvalidOperationException("Place the carrier on Station 3 and raise the open pickup before moving to the first FOV.");

        await _gantry.MoveToAsync(firstFov, _settings.Speed, cancellationToken);
    }
}
