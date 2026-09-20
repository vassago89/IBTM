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
                canPickUp: true, holdAtDestination: holdAtShuttle) != endState)
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
        await RunToAsync(NgTransferDestination.Station, cancellationToken);
    }

    public async Task ClearStationAsync(AxisPosition? firstFov, CancellationToken cancellationToken)
    {
        if (firstFov is null)
            return;
        if (!_work.Station.CarrierPresent
            || _pickup.Gripper != NgTransferGripperState.Open
            || !_pickup.IsRaised)
            throw new InvalidOperationException("Place the carrier on Station 3 and raise the open pickup before moving to the first FOV.");

        var safeX = _settings.PickupSafeX
            ?? throw new InvalidOperationException("Teach NG Pickup Safe X before leaving Station 3.");
        await _gantry.MoveAxisAsync(MotionAxis.X, safeX, _settings.Speed, cancellationToken);
        await _gantry.MoveAxisAsync(MotionAxis.Y, firstFov.Y, _settings.Speed, cancellationToken);
        await _gantry.MoveAxisAsync(MotionAxis.X, firstFov.X, _settings.Speed, cancellationToken);
    }
}
