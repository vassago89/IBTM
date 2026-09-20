using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;

namespace IBTM.Inspection;

public enum NgTransferDestination
{
    [Description("Station 3")]
    Station,
    [Description("Shuttle")]
    Shuttle,
}

public enum NgTransferState
{
    [Description("Waiting")]
    Idle,

    [Description("Raise Station 3 Backup Plate and Lower Stopper")]
    StationNotReady,

    [Description("Raise the Shuttle")]
    ShuttleNotReady,

    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Destination")]
    WaitingForDestination,

    [Description("Raising Pickup")]
    Raising,

    [Description("Opening Gripper")]
    Opening,

    [Description("Picking Carrier")]
    PickingCarrier,

    [Description("Securing Carrier Grip")]
    GrippingCarrier,

    [Description("Moving and Lowering Carrier at Destination")]
    PlacingCarrier,

    [Description("Waiting for Placed Carrier")]
    WaitingForPlacement,

    [Description("Transfer Complete")]
    Completed,

    [Description("Carrier Held at Destination")]
    HoldingAtDestination,
}

// Automatic operation and dry run use the same physical transfer.
public sealed partial class NgCarrierMove : AutoUnit
{
    private readonly InspectionWork _work;
    private readonly NgShuttle _shuttle;
    private readonly NgCarrierTransfer _pickup;
    private readonly InspectionGantry _gantry;
    private readonly NgCarrierTransferSettings _settings;
    private readonly UnitSettings _units;

    public NgCarrierMove(
        InspectionWork station,
        NgShuttle shuttle,
        NgCarrierTransfer pickup,
        InspectionGantry gantry,
        NgCarrierTransferSettings settings,
        UnitSettings units)
    {
        _work = station;
        _shuttle = shuttle;
        _pickup = pickup;
        _gantry = gantry;
        _settings = settings;
        _units = units;
    }

    public bool IsEmptyRepeatAllowed => !_units.MainConveyor && !_units.Inspection
        && !_units.NgShuttle && !_units.NgConveyor;

    public override event Action? Changed
    {
        add
        {
            _work.Changed += value;
            _shuttle.Changed += value;
            _pickup.Changed += value;
            _gantry.Feedback.StateChanged += value;
        }

        remove
        {
            _work.Changed -= value;
            _shuttle.Changed -= value;
            _pickup.Changed -= value;
            _gantry.Feedback.StateChanged -= value;
        }
    }

    public bool IsCarrierPresent(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? _work.Station.CarrierPresent
            : _shuttle.Feedback.CarrierDetected;
    }

    public NgTransferState GetState(
        NgTransferDestination destination,
        bool canPickUp,
        bool canReceive = true,
        bool holdAtDestination = false,
        bool live = true,
        bool allowEmpty = false)
    {
        var source = GetOppositeDestination(destination);
        var destinationPosition = GetTransferPosition(destination);
        var sourcePosition = GetTransferPosition(source);
        var atDestination = destinationPosition is not null && _gantry.IsAt(destinationPosition, live);
        var atSource = sourcePosition is not null && _gantry.IsAt(sourcePosition, live);
        // Transfer-only repeat keeps the carrier gripped; the disabled shuttle is not a support.
        var holdAtShuttle = holdAtDestination && destination == NgTransferDestination.Shuttle;
        var destinationPresent = !holdAtShuttle && IsCarrierPresent(destination);
        var down = _pickup.Lift == NgTransferLiftState.Down;
        var open = _pickup.Gripper == NgTransferGripperState.Open;
        switch (true)
        {
            case true when holdAtDestination && atDestination && down && _pickup.IsTransferPending:
                switch (true)
                {
                    case true when !holdAtShuttle && !IsSupportReady(destination):
                        return GetSupportWaitingState(destination);
                    default:
                        return _pickup.Gripper == NgTransferGripperState.Closed
                            && (allowEmpty || _pickup.CarrierDetected)
                            ? NgTransferState.HoldingAtDestination
                            : NgTransferState.GrippingCarrier;
                }
            case true when !holdAtShuttle && atDestination && open && (destinationPresent || allowEmpty):
                return _pickup.IsRaised ? NgTransferState.Completed : NgTransferState.Raising;
            case true when !holdAtShuttle
                && atDestination
                && open
                && !_pickup.IsRaised
                && !IsCarrierPresent(source):
                return NgTransferState.WaitingForPlacement;
            case true when atDestination && down && (destinationPresent || allowEmpty):
                return IsSupportReady(destination) ? NgTransferState.Opening : GetSupportWaitingState(destination);
            case true when _pickup.IsTransferPending:
                switch (true)
                {
                    case true when _pickup.Gripper != NgTransferGripperState.Closed
                        || !allowEmpty && !_pickup.CarrierDetected:
                        return NgTransferState.GrippingCarrier;
                    case true when !atDestination && !_pickup.IsRaised:
                        return NgTransferState.Raising;
                    case true when !holdAtShuttle && !IsSupportReady(destination):
                        return GetSupportWaitingState(destination);
                    case true when atDestination && !_pickup.IsRaised:
                        return down ? NgTransferState.Opening : NgTransferState.PlacingCarrier;
                    case true when destinationPresent || !canReceive:
                        return NgTransferState.WaitingForDestination;
                    default:
                        return NgTransferState.PlacingCarrier;
                }
            case true when !_pickup.IsRaised && (!canPickUp || !atSource):
                return NgTransferState.Raising;
            case true when !canPickUp:
                return open ? NgTransferState.Idle : NgTransferState.Opening;
            case true when !allowEmpty && !IsCarrierPresent(source):
                return NgTransferState.WaitingForCarrier;
            case true when destinationPresent:
                return NgTransferState.WaitingForDestination;
            case true when !IsSupportReady(source):
                return GetSupportWaitingState(source);
            case true when !holdAtShuttle && !IsSupportReady(destination):
                return GetSupportWaitingState(destination);
            case true when atSource:
                return down ? NgTransferState.GrippingCarrier
                    : open ? NgTransferState.PickingCarrier : NgTransferState.Opening;
            default:
                return open ? NgTransferState.PickingCarrier : NgTransferState.Opening;
        }
    }

    public async Task RunToAsync(
        NgTransferDestination destination,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        BeginRun();
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && GetState(destination, canPickUp: true, allowEmpty: allowEmpty) != NgTransferState.Completed)
            {
                if (!await ExecuteAsync(destination,
                    GetState(destination, canPickUp: true, allowEmpty: allowEmpty), cancellationToken,
                    allowEmpty: allowEmpty))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            EndRun(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    // False means the caller can wait or perform inspection while the transfer is idle.
    public async Task<bool> ExecuteAsync(
        NgTransferDestination destination,
        NgTransferState state,
        CancellationToken cancellationToken,
        bool holdAtDestination = false,
        bool allowEmpty = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TraceStep(state, destination.ToString(), _work.CurrentJob.Id);
        switch (state)
        {
            case NgTransferState.Raising:
                await _pickup.SetLiftUpAsync(true, cancellationToken);
                break;
            case NgTransferState.Opening:
                await _pickup.SetGripperOpenAsync(true, cancellationToken);
                break;
            case NgTransferState.PickingCarrier:
                var source = GetOppositeDestination(destination);
                await MoveToCarrierAsync(source, cancellationToken);
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                await _pickup.SetLiftUpAsync(false, cancellationToken);
                await _pickup.GripForTransferAsync(cancellationToken, allowEmpty);
                await _pickup.SetLiftUpAsync(true, cancellationToken);
                break;
            case NgTransferState.GrippingCarrier:
                var gripSource = GetOppositeDestination(destination);
                if (!_pickup.IsTransferPending
                    && (!IsSupportReady(gripSource)
                        || !allowEmpty && !IsCarrierPresent(gripSource)
                        || _pickup.Lift != NgTransferLiftState.Down
                        || GetTransferPosition(gripSource) is not { } gripPosition
                        || !_gantry.IsAt(gripPosition)))
                    return false;
                await _pickup.GripForTransferAsync(cancellationToken, allowEmpty);
                break;
            case NgTransferState.PlacingCarrier:
                var position = GetTransferPosition(destination)
                    ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before returning to Station 3.");
                if (!_gantry.IsAt(position))
                    await _gantry.MoveToAsync(position, _settings.Speed, cancellationToken);
                // The support may change while XY is moving; do not lower onto it blindly.
                if (!IsSupportReady(destination)
                    && !(holdAtDestination && destination == NgTransferDestination.Shuttle))
                    return false;
                await _pickup.SetLiftUpAsync(false, cancellationToken);
                break;
            case NgTransferState.WaitingForPlacement:
                if (destination == NgTransferDestination.Shuttle)
                    await _shuttle.WaitForCarrierAsync(cancellationToken);
                else
                    await _work.Station.WaitForCarrierAsync(cancellationToken);
                break;
            default:
                return false;
        }
        return true;
    }

    private static NgTransferDestination GetOppositeDestination(NgTransferDestination destination)
    {
        return destination == NgTransferDestination.Shuttle
            ? NgTransferDestination.Station
            : NgTransferDestination.Shuttle;
    }

    public async Task MoveToCarrierAsync(
        NgTransferDestination source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = GetTransferPosition(source)
            ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before moving to a carrier.");
        if (_gantry.IsAt(position))
            return;
        await _gantry.MoveToAsync(position, _settings.Speed, cancellationToken);
    }

    private AxisPosition? GetTransferPosition(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? _settings.GetCarrierPickupPosition()
            : _settings.ShuttlePlacePosition;
    }

    private bool IsSupportReady(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? _work.Station.BackupPlate == StationCylinderState.Up
                && _work.Station.Stopper == StationCylinderState.Down
            : _shuttle.Feedback.Lift == NgShuttleLiftState.Up;
    }

    private static NgTransferState GetSupportWaitingState(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? NgTransferState.StationNotReady
            : NgTransferState.ShuttleNotReady;
    }
}
