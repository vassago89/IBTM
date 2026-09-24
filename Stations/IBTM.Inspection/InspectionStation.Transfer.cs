using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.Inspection;

public sealed partial class InspectionStation
{
    public InspectionStationState GetNextTransferStep(
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
        var atDestination = destinationPosition is not null && IsAt(destinationPosition, live);
        var atSource = sourcePosition is not null && IsAt(sourcePosition, live);
        // Repeat turns around above the shuttle with the carrier still raised and gripped.
        var holdAtShuttle = holdAtDestination && destination == NgTransferDestination.Shuttle;
        var destinationPresent = !holdAtShuttle && IsCarrierPresent(destination);
        var lift = Lift;
        var gripper = Gripper;
        var pending = IsTransferPending;
        var raised = lift == NgTransferLiftState.Up;
        var down = lift == NgTransferLiftState.Down;
        var open = gripper == NgTransferGripperState.Open;
        var destinationReady = holdAtShuttle || IsSupportReady(destination);
        // S3 support can be prepared after XY travel, with the pickup still raised.
        var canPrepareStation = destination == NgTransferDestination.Station && raised;

        if (atDestination)
        {
            if (holdAtDestination && (holdAtShuttle ? raised : down) && pending)
            {
                if (!destinationReady)
                    return InspectionStationState.WaitingForDestination;
                return gripper == NgTransferGripperState.Closed
                    ? InspectionStationState.HoldingAtDestination : InspectionStationState.PickingCarrier;
            }

            if (!holdAtShuttle)
            {
                if (open && !pending && (destinationPresent || allowEmpty))
                    return raised ? InspectionStationState.TransferCompleted : InspectionStationState.PlacingCarrier;
                if (open && !pending && !raised && !IsCarrierPresent(source))
                    return InspectionStationState.PlacingCarrier;
                // Supported release can resume even while the gripper is between its sensors.
                if (down && (destinationPresent || allowEmpty))
                    return destinationReady ? InspectionStationState.PlacingCarrier : InspectionStationState.WaitingForDestination;
            }
        }

        if (pending)
        {
            if (gripper != NgTransferGripperState.Closed)
                return InspectionStationState.PickingCarrier;
            if (!raised && (!atDestination || holdAtShuttle))
                return InspectionStationState.PreparingTransfer;
            if (!destinationReady && !canPrepareStation)
                return InspectionStationState.WaitingForDestination;
            if (atDestination && !raised)
                return InspectionStationState.PlacingCarrier;
            return destinationPresent || !canReceive
                ? InspectionStationState.WaitingForDestination : InspectionStationState.PlacingCarrier;
        }

        if (!raised && (!canPickUp || !canReceive || !atSource))
            return InspectionStationState.PreparingTransfer;
        if (!canPickUp)
            return open ? InspectionStationState.Waiting : InspectionStationState.PreparingTransfer;
        if (!canReceive)
            return open ? InspectionStationState.WaitingForDestination : InspectionStationState.PreparingTransfer;
        if (!allowEmpty && !IsCarrierPresent(source))
            return InspectionStationState.Waiting;
        if (destinationPresent || !IsSupportReady(source) || !destinationReady && !canPrepareStation)
            return InspectionStationState.WaitingForDestination;
        return (atSource && down) || open
            ? InspectionStationState.PickingCarrier : InspectionStationState.PreparingTransfer;
    }

    public async Task RunToAsync(
        NgTransferDestination destination,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        BeginRun();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = GetNextTransferStep(destination, canPickUp: true, allowEmpty: allowEmpty);
                if (state == InspectionStationState.TransferCompleted)
                    break;
                if (!await ExecuteTransferAsync(destination, state, cancellationToken,
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
    public async Task<bool> ExecuteTransferAsync(
        NgTransferDestination destination,
        InspectionStationState state,
        CancellationToken cancellationToken,
        bool holdAtDestination = false,
        bool allowEmpty = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterStep(state, destination.ToString(), waitingFor: state == InspectionStationState.WaitingForDestination
            ? $"source support={IsSupportReady(GetOppositeDestination(destination))}, "
                + $"destination support={IsSupportReady(destination)}, destination occupied={IsCarrierPresent(destination)}, "
                + $"shuttle={_ngConveyor.ShuttleLift}, receive={_ngConveyor.IsReceiveAllowed()}, "
                + $"pickup={Lift}, gripper={Gripper}, pending={IsTransferPending}"
            : null);
        switch (state)
        {
            case InspectionStationState.PreparingTransfer:
                if (!IsRaised)
                    await SetLiftUpAsync(true, cancellationToken);
                if (!IsTransferPending && Gripper != NgTransferGripperState.Open)
                    await SetGripperOpenAsync(true, cancellationToken);
                break;
            case InspectionStationState.PickingCarrier:
                var source = GetOppositeDestination(destination);
                if (IsTransferPending
                    && (!IsSupportReady(source)
                        || !allowEmpty && !IsCarrierPresent(source)
                        || Lift != NgTransferLiftState.Down
                        || GetTransferPosition(source) is not { } gripPosition
                        || !IsAt(gripPosition)))
                {
                    throw new InvalidOperationException("NG transfer grip is uncertain away from its supported pickup position. Check the carrier before resuming.");
                }
                await MoveToCarrierAsync(source, cancellationToken);
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                if (Lift != NgTransferLiftState.Down)
                    await SetLiftUpAsync(false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Descent can outlive the source's support or carrier feedback.
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                IsTransferPending = true;
                await SetGripperOpenAsync(false, cancellationToken);
                await SetLiftUpAsync(true, cancellationToken);
                break;
            case InspectionStationState.PlacingCarrier:
            {
                var position = GetTransferPosition(destination)
                    ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before returning to Station 3.");
                var supported = IsAt(position) && Lift == NgTransferLiftState.Down
                    && IsSupportReady(destination) && (allowEmpty || IsCarrierPresent(destination));
                if (IsTransferPending && (!supported || holdAtDestination))
                {
                    using var carrying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    void CheckGrip()
                    {
                        if (!IsTransferPending || Gripper != NgTransferGripperState.Closed)
                            carrying.Cancel();
                    }
                    Changed += CheckGrip;
                    try
                    {
                        CheckGrip();
                        carrying.Token.ThrowIfCancellationRequested();
                        if (destination == NgTransferDestination.Station && !IsSupportReady(destination))
                            await SeatStationAsync(carrying.Token);
                        else if (!IsAt(position))
                            await MoveToAsync(position, cancellationToken: carrying.Token);
                        CheckGrip();
                        carrying.Token.ThrowIfCancellationRequested();
                        if (holdAtDestination && destination == NgTransferDestination.Shuttle)
                            return true;
                        // Recheck the support after XY travel before lowering.
                        if (!IsSupportReady(destination))
                            return false;
                        if (Lift != NgTransferLiftState.Down)
                            await SetLiftUpAsync(false, carrying.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException("NG transfer lost confirmed grip while carrying or lowering the carrier.");
                    }
                    finally
                    {
                        Changed -= CheckGrip;
                    }
                }

                if (holdAtDestination && IsTransferPending)
                    break;
                if (!IsAt(position) || !IsSupportReady(destination))
                    return false;
                if (IsTransferPending || Gripper != NgTransferGripperState.Open)
                {
                    if (Lift != NgTransferLiftState.Down || !allowEmpty && !IsCarrierPresent(destination))
                        throw new InvalidOperationException("Confirm the destination supports the pending NG carrier before releasing it.");
                    if (destination == NgTransferDestination.Shuttle)
                        _waitingForShuttleDown = true;
                    await SetGripperOpenAsync(true, cancellationToken);
                }
                if (!allowEmpty)
                {
                    if (destination == NgTransferDestination.Shuttle)
                        await _io.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true, cancellationToken);
                    else
                        await Station.WaitForCarrierAsync(cancellationToken);
                }
                if (!IsRaised)
                    await SetLiftUpAsync(true, cancellationToken);
                break;
            }
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
        if (IsAt(position))
            return;
        await MoveToAsync(position, cancellationToken: cancellationToken);
    }

    public async Task SeatStationAsync(CancellationToken cancellationToken)
    {
        var position = _settings.CarrierPickupPosition
            ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before raising the inspection backup plate.");
        // This awaited sequence owns XY until the plate finishes rising.
        // Do not infer permission to raise the plate from a coordinate comparison.
        await MoveToAsync(position, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await Station.SeatAsync(cancellationToken);
    }

    private AxisPosition? GetTransferPosition(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? _settings.CarrierPickupPosition
            : _settings.ShuttlePlacePosition;
    }

    private bool IsSupportReady(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.BackupPlate == StationCylinderState.Up
                && Station.Stopper == StationCylinderState.Down
            : _io.GetInput(InputIo.NgShuttleUp) && !_io.GetInput(InputIo.NgShuttleDown);
    }
}
