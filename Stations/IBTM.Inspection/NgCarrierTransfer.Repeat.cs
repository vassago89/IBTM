using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed partial class NgCarrierTransfer
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
        await RunToAsync(NgTransferDestination.Station, cancellationToken,
            allowEmpty: IsEmptyRepeatAllowed);
    }

    public async Task ClearStationAsync(AxisPosition? waitingPosition, CancellationToken cancellationToken)
    {
        if (waitingPosition is null)
            return;
        if ((!IsEmptyRepeatAllowed && !Station.CarrierPresent)
            || IsTransferPending
            || Gripper != NgTransferGripperState.Open
            || !IsRaised)
            throw new InvalidOperationException("Place the carrier on Station 3 and raise the open pickup before moving to the Data Matrix waiting position.");

        await MoveToAsync(waitingPosition, _settings.Speed, cancellationToken);
    }
}
