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
    [Description("Raise Station 3 backup plate and lower its stopper")]
    StationNotReady,
    [Description("Raise the shuttle")]
    ShuttleNotReady,
    [Description("Waiting for carrier")]
    WaitingForCarrier,
    [Description("Waiting for destination")]
    WaitingForDestination,
    [Description("Raising pickup")]
    Raising,
    [Description("Opening gripper")]
    Opening,
    [Description("Moving to carrier")]
    MovingToCarrier,
    [Description("Lowering to carrier")]
    LoweringToCarrier,
    [Description("Closing gripper")]
    Closing,
    [Description("Waiting for carrier grip")]
    WaitingForGrip,
    [Description("Moving to destination")]
    MovingToDestination,
    [Description("Lowering at destination")]
    LoweringAtDestination,
    [Description("Waiting for placed carrier")]
    WaitingForPlacement,
    [Description("Transfer complete")]
    Completed,
    [Description("Carrier held at destination")]
    HoldingAtDestination,
}

// Automatic operation and dry run use the same physical transfer.
public sealed class NgCarrierMove : AutoUnit
{
    private readonly InspectionWork _work;
    private readonly NgShuttle _shuttle;
    private readonly NgCarrierTransfer _pickup;
    private readonly InspectionGantry _gantry;
    private readonly NgCarrierTransferSettings _settings;

    public NgCarrierMove(
        InspectionWork station,
        NgShuttle shuttle,
        NgCarrierTransfer pickup,
        InspectionGantry gantry,
        NgCarrierTransferSettings settings)
    {
        _work = station;
        _shuttle = shuttle;
        _pickup = pickup;
        _gantry = gantry;
        _settings = settings;
    }

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
        bool live = true)
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
            case true when holdAtDestination && atDestination && down:
                switch (true)
                {
                    case true when !holdAtShuttle && !IsSupportReady(destination):
                        return GetSupportWaitingState(destination);
                    case true when _pickup.Gripper != NgTransferGripperState.Closed:
                        return NgTransferState.Closing;
                    default:
                        return _pickup.CarrierDetected
                            ? NgTransferState.HoldingAtDestination
                            : NgTransferState.WaitingForGrip;
                }
            // Released carriers may still be visible to the pickup's presence sensor.
            case true when !holdAtShuttle && atDestination && open && destinationPresent:
                return _pickup.IsRaised ? NgTransferState.Completed : NgTransferState.Raising;
            case true when !holdAtShuttle
                && atDestination
                && open
                && !_pickup.IsRaised
                && (_pickup.CarrierDetected || !IsCarrierPresent(source)):
                return NgTransferState.WaitingForPlacement;
            case true when atDestination && down && destinationPresent:
                return IsSupportReady(destination) ? NgTransferState.Opening : GetSupportWaitingState(destination);
            case true when _pickup.CarrierDetected:
                switch (true)
                {
                    case true when _pickup.Gripper != NgTransferGripperState.Closed:
                        return NgTransferState.Closing;
                    case true when !atDestination && !_pickup.IsRaised:
                        return NgTransferState.Raising;
                    case true when !holdAtShuttle && !IsSupportReady(destination):
                        return GetSupportWaitingState(destination);
                    case true when atDestination && !_pickup.IsRaised:
                        return down ? NgTransferState.Opening : NgTransferState.LoweringAtDestination;
                    case true when destinationPresent || !canReceive:
                        return NgTransferState.WaitingForDestination;
                    default:
                        return atDestination
                            ? NgTransferState.LoweringAtDestination
                            : NgTransferState.MovingToDestination;
                }
            case true when atSource && down && _pickup.Gripper == NgTransferGripperState.Closed:
                return NgTransferState.WaitingForGrip;
            case true when !_pickup.IsRaised && (!canPickUp || !atSource):
                return NgTransferState.Raising;
            case true when !canPickUp:
                return open ? NgTransferState.Idle : NgTransferState.Opening;
            case true when !IsCarrierPresent(source):
                return NgTransferState.WaitingForCarrier;
            case true when destinationPresent:
                return NgTransferState.WaitingForDestination;
            case true when !IsSupportReady(source):
                return GetSupportWaitingState(source);
            case true when !holdAtShuttle && !IsSupportReady(destination):
                return GetSupportWaitingState(destination);
            case true when atSource:
                return down
                    ? NgTransferState.Closing
                    : open ? NgTransferState.LoweringToCarrier : NgTransferState.Opening;
            default:
                return open ? NgTransferState.MovingToCarrier : NgTransferState.Opening;
        }
    }

    public async Task RunToAsync(
        NgTransferDestination destination,
        CancellationToken cancellationToken)
    {
        BeginRun();
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && GetState(destination, canPickUp: true) != NgTransferState.Completed)
            {
                await (ExecuteAsync(destination, GetState(destination, canPickUp: true), cancellationToken)
                    ?? WaitForChangeAsync(cancellationToken));
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

    // Passive states issue no command; the caller waits or performs its other work.
    public Task? ExecuteAsync(
        NgTransferDestination destination,
        NgTransferState state,
        CancellationToken cancellationToken)
    {
        TraceStep(state, destination.ToString(), _work.CurrentJob.Id);
        switch (state)
        {
            case NgTransferState.Raising:
                return _pickup.SetLiftUpAsync(true, cancellationToken);
            case NgTransferState.Opening:
                return _pickup.SetGripperOpenAsync(true, cancellationToken);
            case NgTransferState.Closing:
                return _pickup.SetGripperOpenAsync(false, cancellationToken);
            case NgTransferState.LoweringToCarrier or NgTransferState.LoweringAtDestination:
                return _pickup.SetLiftUpAsync(false, cancellationToken);
            case NgTransferState.MovingToCarrier:
                return MoveToCarrierAsync(GetOppositeDestination(destination), cancellationToken);
            case NgTransferState.MovingToDestination:
                return _gantry.MoveToAsync(
                    GetTransferPosition(destination)
                        ?? throw new InvalidOperationException("Teach NG Pickup Safe X before returning to Station 3."),
                    _settings.Speed,
                    cancellationToken);
            case NgTransferState.WaitingForGrip:
                return _pickup.WaitForCarrierGripAsync(cancellationToken);
            case NgTransferState.WaitingForPlacement:
                return destination == NgTransferDestination.Shuttle
                    ? _shuttle.WaitForCarrierAsync(cancellationToken)
                    : _work.Station.WaitForCarrierAsync(cancellationToken);
            default:
                return null;
        }
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
            ?? throw new InvalidOperationException("Teach NG Pickup Safe X before moving to a carrier.");
        if (_gantry.IsAt(position))
            return;
        var safeX = _settings.PickupSafeX
            ?? throw new InvalidOperationException("Teach NG Pickup Safe X before moving to a carrier.");
        await _gantry.MoveAxisAsync(MotionAxis.X, safeX, _settings.Speed, cancellationToken);
        await _gantry.MoveAxisAsync(MotionAxis.Y, position.Y, _settings.Speed, cancellationToken);
        if (source == NgTransferDestination.Shuttle)
            await _gantry.MoveAxisAsync(MotionAxis.X, position.X, _settings.Speed, cancellationToken);
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
