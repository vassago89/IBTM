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
public sealed class NgCarrierMove(
    InspectionWork station,
    NgShuttle shuttle,
    NgCarrierTransfer pickup,
    InspectionGantry gantry,
    NgCarrierTransferSettings settings) : AutoUnit
{
    public override event Action? Changed
    {
        add
        {
            station.Changed += value;
            shuttle.Changed += value;
            pickup.Changed += value;
            gantry.Feedback.StateChanged += value;
        }

        remove
        {
            station.Changed -= value;
            shuttle.Changed -= value;
            pickup.Changed -= value;
            gantry.Feedback.StateChanged -= value;
        }
    }

    public bool CarrierPresent(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? station.CarrierPresent
            : shuttle.Feedback.CarrierDetected;
    }

    public NgTransferState State(
        NgTransferDestination destination,
        bool canPickUp,
        bool canReceive = true,
        bool holdAtDestination = false)
    {
        var source = Opposite(destination);
        var destinationPosition = Position(destination);
        var sourcePosition = Position(source);
        var atDestination = destinationPosition is not null && gantry.IsAt(destinationPosition);
        var atSource = sourcePosition is not null && gantry.IsAt(sourcePosition);
        // Transfer-only repeat keeps the carrier gripped; the disabled shuttle is not a support.
        var holdAtShuttle = holdAtDestination && destination == NgTransferDestination.Shuttle;
        var destinationPresent = !holdAtShuttle && CarrierPresent(destination);
        var down = pickup.Lift == NgTransferLiftState.Down;
        var open = pickup.Gripper == NgTransferGripperState.Open;
        if (holdAtDestination && atDestination && down)
        {
            if (!holdAtShuttle && !SupportReady(destination))
                return SupportNotReady(destination);
            if (pickup.Gripper != NgTransferGripperState.Closed)
                return NgTransferState.Closing;
            return pickup.CarrierDetected
                ? NgTransferState.HoldingAtDestination
                : NgTransferState.WaitingForGrip;
        }

        // Released carriers may still be visible to the pickup's presence sensor.
        if (atDestination && open && destinationPresent)
            return pickup.IsRaised ? NgTransferState.Completed : NgTransferState.Raising;
        if (atDestination
            && open
            && !pickup.IsRaised
            && (pickup.CarrierDetected || !CarrierPresent(source)))
            return NgTransferState.WaitingForPlacement;
        if (atDestination && down && destinationPresent)
            return SupportReady(destination) ? NgTransferState.Opening : SupportNotReady(destination);

        if (pickup.CarrierDetected)
        {
            if (pickup.Gripper != NgTransferGripperState.Closed)
                return NgTransferState.Closing;
            if (!atDestination && !pickup.IsRaised)
                return NgTransferState.Raising;
            if (!holdAtShuttle && !SupportReady(destination))
                return SupportNotReady(destination);
            if (atDestination && !pickup.IsRaised)
                return down ? NgTransferState.Opening : NgTransferState.LoweringAtDestination;
            if (destinationPresent || !canReceive)
                return NgTransferState.WaitingForDestination;
            return atDestination
                ? NgTransferState.LoweringAtDestination
                : NgTransferState.MovingToDestination;
        }

        if (atSource && down && pickup.Gripper == NgTransferGripperState.Closed)
            return NgTransferState.WaitingForGrip;
        if (!pickup.IsRaised && (!canPickUp || !atSource))
            return NgTransferState.Raising;
        if (!canPickUp)
            return open ? NgTransferState.Idle : NgTransferState.Opening;
        if (!CarrierPresent(source))
            return NgTransferState.WaitingForCarrier;
        if (destinationPresent)
            return NgTransferState.WaitingForDestination;
        if (!SupportReady(source))
            return SupportNotReady(source);
        if (!holdAtShuttle && !SupportReady(destination))
            return SupportNotReady(destination);
        if (atSource)
            return down
                ? NgTransferState.Closing
                : open ? NgTransferState.LoweringToCarrier : NgTransferState.Opening;
        return open ? NgTransferState.MovingToCarrier : NgTransferState.Opening;
    }

    public async Task RunToAsync(
        NgTransferDestination destination,
        CancellationToken cancellationToken)
    {
        await RunLoopAsync(
            token => ExecuteAsync(destination, State(destination, canPickUp: true), token)
                ?? WaitForChangeAsync(token),
            cancellationToken,
            () => State(destination, canPickUp: true) == NgTransferState.Completed);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task ReturnToStationAsync(CancellationToken cancellationToken)
    {
        await station.Station.SeatAsync(cancellationToken);
        await RunToAsync(NgTransferDestination.Station, cancellationToken);
    }

    public async Task ClearStationAsync(AxisPosition? firstFov, CancellationToken cancellationToken)
    {
        if (firstFov is null)
            return;
        if (!station.CarrierPresent
            || pickup.Gripper != NgTransferGripperState.Open
            || !pickup.IsRaised)
            throw new InvalidOperationException("Place the carrier on Station 3 and raise the open pickup before moving to the first FOV.");

        var safeX = settings.PickupSafeX
            ?? throw new InvalidOperationException("Teach NG Pickup Safe X before leaving Station 3.");
        await gantry.MoveAxisAsync(MotionAxis.X, safeX, settings.Speed, cancellationToken);
        await gantry.MoveAxisAsync(MotionAxis.Y, firstFov.Y, settings.Speed, cancellationToken);
        await gantry.MoveAxisAsync(MotionAxis.X, firstFov.X, settings.Speed, cancellationToken);
    }

    // Passive states issue no command; the caller waits or performs its other work.
    public Task? ExecuteAsync(
        NgTransferDestination destination,
        NgTransferState state,
        CancellationToken cancellationToken)
    {
        return state switch
        {
            NgTransferState.Raising => pickup.RaiseAsync(cancellationToken),
            NgTransferState.Opening => pickup.SetGripperOpenAsync(true, cancellationToken),
            NgTransferState.Closing => pickup.SetGripperOpenAsync(false, cancellationToken),
            NgTransferState.LoweringToCarrier or NgTransferState.LoweringAtDestination
                => pickup.SetLiftUpAsync(false, cancellationToken),
            NgTransferState.MovingToCarrier
                => MoveToCarrierAsync(Opposite(destination), cancellationToken),
            NgTransferState.MovingToDestination
                => gantry.MoveToAsync(
                    Position(destination)
                        ?? throw new InvalidOperationException("Teach NG Pickup Safe X before returning to Station 3."),
                    settings.Speed,
                    cancellationToken),
            NgTransferState.WaitingForGrip => pickup.WaitForCarrierGripAsync(cancellationToken),
            NgTransferState.WaitingForPlacement
                => destination == NgTransferDestination.Shuttle
                    ? shuttle.WaitForCarrierAsync(cancellationToken)
                    : station.Station.WaitForCarrierAsync(cancellationToken),
            _ => null,
        };
    }

    private static NgTransferDestination Opposite(NgTransferDestination destination)
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
        var position = Position(source)
            ?? throw new InvalidOperationException("Teach NG Pickup Safe X before moving to a carrier.");
        if (gantry.IsAt(position))
            return;
        var safeX = settings.PickupSafeX
            ?? throw new InvalidOperationException("Teach NG Pickup Safe X before moving to a carrier.");
        await gantry.MoveAxisAsync(MotionAxis.X, safeX, settings.Speed, cancellationToken);
        await gantry.MoveAxisAsync(MotionAxis.Y, position.Y, settings.Speed, cancellationToken);
        if (source == NgTransferDestination.Shuttle)
            await gantry.MoveAxisAsync(MotionAxis.X, position.X, settings.Speed, cancellationToken);
    }

    private AxisPosition? Position(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? settings.GetCarrierPickupPosition()
            : settings.ShuttlePlacePosition;
    }

    private bool SupportReady(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? station.BackupPlate == StationCylinderState.Up
                && station.Stopper == StationCylinderState.Down
            : shuttle.Feedback.Lift == NgShuttleLiftState.Up;
    }

    private static NgTransferState SupportNotReady(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? NgTransferState.StationNotReady
            : NgTransferState.ShuttleNotReady;
    }
}
