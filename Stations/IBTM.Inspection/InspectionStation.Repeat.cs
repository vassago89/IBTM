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
                || _settings.ShuttlePlacePosition is not { } position || !MotionServiceBase.IsAt(_motion, position))
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
        try
        {
            BeginRun();
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = GetNextTransferStep(NgTransferDestination.Station, canPickUp: true, repeat: true);
                if (state == InspectionStationState.TransferCompleted)
                    break;
                if (!await ExecuteTransferAsync(NgTransferDestination.Station, state, cancellationToken, repeat: true))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        finally
        {
            EndRun(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
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

        await MoveToAsync(waitingPosition, cancellationToken: cancellationToken);
    }
}
