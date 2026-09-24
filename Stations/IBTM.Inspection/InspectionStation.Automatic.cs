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
    public async Task RunAsync(
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        BeginRun();
        try
        {
            if (Enabled)
                Station.Restart(Station.CurrentJob);
            while (!cancellationToken.IsCancellationRequested)
            {
                var step = !Enabled && !CarrierSeatingRequested
                    ? InspectionStationState.Disabled : GetNextStep(repeat);
                if (!await ExecuteStepAsync(step, repeat, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            ClearInspectionOperation();
            ClearCarrierSeatingRequest();
            EndRun(cancellationToken);
        }
    }

    public InspectionStationState GetNextStep(
        bool repeat = false,
        bool live = true,
        bool? conveyorRunning = null,
        bool? mainConveyorRunning = null)
    {
        if (_waitingForShuttleDown && IsClear && Gripper == NgTransferGripperState.Open)
            return _ngConveyor.ShuttleLift == NgShuttleLiftState.Down
                ? InspectionStationState.ReturningToWaitingPosition
                : InspectionStationState.WaitingForShuttleDown;
        if (CarrierSeatingRequested)
            return InspectionStationState.SeatingCarrier;
        if (repeat && Enabled && !_units.MainConveyor
            && (IsEmptyRepeatAllowed || Station.CarrierPresent) && IsClear)
        {
            if (Station.Completed || IsEmptyRepeatAllowed && !Station.CarrierPresent)
            {
                if (Station.BackupPlate != StationCylinderState.Up
                    || Station.Stopper != StationCylinderState.Down)
                    return InspectionStationState.SeatingCarrier;
            }
            else if (Station.BackupPlate != StationCylinderState.Down
                || Station.Stopper != StationCylinderState.Up)
                return InspectionStationState.PreparingInspectionPosition;
        }
        if (_units.Inspection)
        {
            var transferState = GetNextTransferStep(
                NgTransferDestination.Shuttle,
                canPickUp: repeat && IsEmptyRepeatAllowed && !Station.CarrierPresent
                    || Station.CarrierSeated && Station.Completed && (repeat || RouteToNg),
                canReceive: repeat || _ngConveyor.IsReceiveAllowed(conveyorRunning),
                holdAtDestination: repeat,
                live: live,
                allowEmpty: repeat && IsEmptyRepeatAllowed);
            if (transferState is not InspectionStationState.Waiting and not InspectionStationState.TransferCompleted)
                return transferState;
        }

        var enabled = Enabled;
        if (!enabled || !IsReadyToInspect(mainConveyorRunning))
        {
            var waiting = !enabled ? InspectionStationState.Disabled
                : IsWaitingForConveyor
                    ? InspectionStationState.WaitingForConveyor
                    : InspectionStationState.Waiting;
            return WaitAtWaitingPosition(waiting, live);
        }
        if (_runJob is null || _inspectionOperation?.IsCancellationRequested == true
            || !ReferenceEquals(_runJob, Station.CurrentJob))
            return InspectionStationState.PreparingInspection;
        var target = InspectionTarget;
        if (target.Pcb is null)
            return InspectionStationState.CompletingInspection;
        if (target.Bolt is null)
        {
            return HasBarcodeRegion(target.Pcb.Value)
                ? InspectionStationState.ReadingBarcode
                : WaitAtWaitingPosition(InspectionStationState.BarcodeTeachingRequired, live);
        }
        return HasRegion(target.Bolt)
            ? InspectionStationState.InspectingBolt
            : WaitAtWaitingPosition(InspectionStationState.FovTeachingRequired, live);
    }

    private async Task<bool> ExecuteStepAsync(
        InspectionStationState state,
        bool repeat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (state)
        {
            case InspectionStationState.PreparingTransfer
                or InspectionStationState.PickingCarrier
                or InspectionStationState.PlacingCarrier
                or InspectionStationState.WaitingForDestination
                or InspectionStationState.HoldingAtDestination:
                return await ExecuteTransferAsync(
                    NgTransferDestination.Shuttle, state, cancellationToken, holdAtDestination: repeat,
                    allowEmpty: repeat && IsEmptyRepeatAllowed);
        }

        EnterStep(state, workId: Station.CurrentJob.Id,
            waitingFor: state == InspectionStationState.WaitingForShuttleDown
                ? $"shuttle Down; current={_ngConveyor.ShuttleLift}"
                : state is InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor
                    ? "carrier, supports and clear pickup" : null);
        switch (state)
        {
            case InspectionStationState.PreparingInspectionPosition:
                await Station.PrepareToReceiveAsync(cancellationToken);
                return true;
            case InspectionStationState.PreparingInspection:
                ClearInspectionOperation();
                BoltPoint[] bolts;
                lock (_recipes.InspectionSync)
                    bolts = _recipes.Current.Pcb.BoltPoints.ToArray();
                _runTargets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
                var points = new List<(HeatSinkSlot Pcb, BoltPoint? Bolt)>();
                foreach (var pcb in _runTargets)
                {
                    var pcbBolts = bolts.Where(bolt => bolt.HeatSink == pcb).OrderBy(bolt => bolt.Number).ToArray();
                    if (pcbBolts.Length == 0)
                        throw new InvalidOperationException(
                            $"{pcb.GetDescription()} has no taught bolts. Complete bolt teaching before inspection.");
                    points.Add((pcb, null));
                    foreach (var bolt in pcbBolts)
                        points.Add((pcb, bolt));
                }
                _runJob = Station.CurrentJob;
                _runPoints = points.ToArray();
                _inspectionOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Changed += CheckInspectionPosition;
                CheckInspectionPosition();
                NotifyChanged();
                return true;
            case InspectionStationState.SeatingCarrier:
                await SeatStationAsync(cancellationToken);
                ClearCarrierSeatingRequest();
                return true;
            case InspectionStationState.ReturningToWaitingPosition:
                await MoveToWaitingPositionAsync(cancellationToken);
                return true;
            case InspectionStationState.Disabled:
                if (Station.CarrierSeated || AtInspectionPosition)
                    Station.Complete(Station.CurrentJob);
                return false;
            case InspectionStationState.Waiting
                or InspectionStationState.WaitingForConveyor
                or InspectionStationState.WaitingForShuttleDown
                or InspectionStationState.BarcodeTeachingRequired
                or InspectionStationState.FovTeachingRequired:
                return false;
        }

        var operation = _inspectionOperation
            ?? throw new InvalidOperationException("No inspection work is selected.");
        var job = _runJob!;
        var token = operation.Token;
        try
        {
            CheckInspectionPosition();
            token.ThrowIfCancellationRequested();
            Station.RequireCurrentJob(job);
            if (state == InspectionStationState.CompletingInspection)
            {
                await MoveToWaitingPositionAsync(token);
                token.ThrowIfCancellationRequested();
                foreach (var heatSink in _runTargets!)
                    Station.GetAssembly(job, heatSink).CompleteInspection();
                Station.Complete(job);
                ClearInspectionOperation();
                return true;
            }

            var target = InspectionTarget;
            var pcb = target.Pcb ?? throw new InvalidOperationException("No inspection target is selected.");
            var assembly = Station.GetAssembly(job, pcb);
            switch (state)
            {
                case InspectionStationState.ReadingBarcode:
                    EnterStep(state, $"{pcb.GetDescription()} / Data Matrix", job.Id);
                    var barcode = await ReadBarcodeAsync(pcb, token);
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    assembly.PcbBarcode = barcode.Barcode;
                    assembly.RecordInspectionCapture(barcode);
                    break;
                case InspectionStationState.InspectingBolt:
                    var bolt = target.Bolt ?? throw new InvalidOperationException("No inspection bolt is selected.");
                    EnterStep(state, $"{pcb.GetDescription()} / Bolt {bolt.Number}", job.Id);
                    var capture = await InspectAsync(bolt, token);
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    assembly.RecordBoltPresence(bolt.Number, capture.Success);
                    assembly.RecordInspectionCapture(capture);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
            _pointIndex++;
            NotifyChanged();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            ClearInspectionOperation();
        }
        return true;
    }

    private void CheckInspectionPosition()
    {
        if (_inspectionOperation is not { } operation)
            return;
        lock (operation)
        {
            if (ReferenceEquals(operation, _inspectionOperation)
                && (!IsReadyToInspect() || !ReferenceEquals(_runJob, Station.CurrentJob)))
                operation.Cancel();
        }
    }

    private void ClearInspectionOperation()
    {
        Changed -= CheckInspectionPosition;
        if (_inspectionOperation is { } operation)
        {
            lock (operation)
            {
                _inspectionOperation = null;
                operation.Dispose();
            }
        }
        _runJob = null;
        _runTargets = null;
        _runPoints = null;
        _pointIndex = 0;
        NotifyChanged();
    }

    private InspectionStationState WaitAtWaitingPosition(InspectionStationState waiting, bool live)
    {
        return IsClear && !IsTransferAtWaitingPosition(live)
            ? InspectionStationState.ReturningToWaitingPosition
            : waiting;
    }

    private async Task MoveToWaitingPositionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = WaitingPosition
            ?? throw new InvalidOperationException("Record Inspection Waiting X/Y before moving to the inspection waiting position.");
        if (!Motion.IsAt(position))
            await MoveToAsync(position, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_waitingForShuttleDown)
        {
            _waitingForShuttleDown = false;
            NotifyChanged();
        }
    }
}
