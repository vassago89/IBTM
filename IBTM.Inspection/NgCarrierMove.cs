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
    [Description("NG transfer is not ready")]
    Unavailable,
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
}

// Automatic operation and dry run use the same physical transfer.
public sealed class NgCarrierMove(
    InspectionWork station,
    NgShuttle shuttle,
    NgCarrierTransfer pickup,
    InspectionGantry gantry,
    NgCarrierTransferSettings settings)
{
    public event Action? Changed
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
        bool canReceive = true)
    {
        var source = Opposite(destination);
        var atDestination = gantry.IsAt(Position(destination));
        var atSource = gantry.IsAt(Position(source));
        var destinationPresent = CarrierPresent(destination);
        var sourcePresent = CarrierPresent(source);
        var down = pickup.Lift == NgTransferLiftState.Down;
        var open = pickup.Gripper == NgTransferGripperState.Open;
        // Released carriers may still be visible to the pickup's presence sensor.
        if (atDestination && open && destinationPresent)
            return pickup.IsRaised ? NgTransferState.Completed : NgTransferState.Raising;
        if (atDestination
            && open
            && !pickup.IsRaised
            && (pickup.CarrierDetected || !sourcePresent))
            return NgTransferState.WaitingForPlacement;
        if (atDestination && down && destinationPresent)
            return SupportReady(destination) ? NgTransferState.Opening : SupportNotReady(destination);

        if (pickup.CarrierDetected)
        {
            if (pickup.Gripper != NgTransferGripperState.Closed)
                return NgTransferState.Closing;
            if (!atDestination && !pickup.IsRaised)
                return NgTransferState.Raising;
            if (!SupportReady(destination))
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
        if (!sourcePresent)
            return NgTransferState.WaitingForCarrier;
        if (destinationPresent)
            return NgTransferState.WaitingForDestination;
        if (!SupportReady(source))
            return SupportNotReady(source);
        if (!SupportReady(destination))
            return SupportNotReady(destination);
        if (atSource)
            return down
                ? NgTransferState.Closing
                : open ? NgTransferState.LoweringToCarrier : NgTransferState.Opening;
        return open ? NgTransferState.MovingToCarrier : NgTransferState.Opening;
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
            NgTransferState.Opening => pickup.SetGripperClosedAsync(false, cancellationToken),
            NgTransferState.Closing => pickup.SetGripperClosedAsync(true, cancellationToken),
            NgTransferState.LoweringToCarrier or NgTransferState.LoweringAtDestination
                => pickup.SetLiftDownAsync(true, cancellationToken),
            NgTransferState.MovingToCarrier
                => gantry.MoveToAsync(Position(Opposite(destination)), settings.Speed, cancellationToken),
            NgTransferState.MovingToDestination
                => gantry.MoveToAsync(Position(destination), settings.Speed, cancellationToken),
            NgTransferState.WaitingForGrip => pickup.WaitForCarrierGripAsync(cancellationToken),
            NgTransferState.WaitingForPlacement
                => destination == NgTransferDestination.Shuttle
                    ? shuttle.WaitForCarrierAsync(true, cancellationToken)
                    : station.Station.WaitForCarrierAsync(cancellationToken),
            _ => null,
        };
    }

    public static NgTransferDestination Opposite(NgTransferDestination destination)
    {
        return destination == NgTransferDestination.Shuttle
            ? NgTransferDestination.Station
            : NgTransferDestination.Shuttle;
    }

    private AxisPosition Position(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? settings.CarrierPickupPosition
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
