using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed partial class InspectionStation
{
    public async Task WaitForRepeatEndAsync(CancellationToken cancellationToken)
    {
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        StepChanged += changed.Set;
        try
        {
            while (Step is not InspectionStationState.HoldingAtDestination
                || !IsTransferPending || !IsRaised || Gripper != NgTransferGripperState.Closed
                || _settings.ShuttlePlacePosition is not { } position || !MotionService.IsAt(_motion, position))
                await changed.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= changed.Set;
            StepChanged -= changed.Set;
        }
    }

    public async Task ReturnToStationAsync(CancellationToken cancellationToken)
    {
        await RunToAsync(NgTransferDestination.Station, cancellationToken,
            allowEmpty: IsEmptyRepeatAllowed);
    }

    public async Task ClearStationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waitingPosition = _settings.WaitingPosition
            ?? throw new InvalidOperationException("Record Inspection Waiting X/Y before moving to the inspection waiting position.");
        if ((!IsEmptyRepeatAllowed && !Station.CarrierPresent)
            || IsTransferPending
            || Gripper != NgTransferGripperState.Open
            || !IsRaised)
            throw new InvalidOperationException("Place the carrier on Station 3 and raise the open pickup before moving to the waiting position.");

        if (!MotionService.IsAt(_motion, waitingPosition))
            await MoveToAsync(waitingPosition, cancellationToken: cancellationToken);
    }
}
