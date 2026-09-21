using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;
using IBTM.Storage;

namespace IBTM.Inspection;

public sealed partial class InspectionStation : AutoUnit
{
    private readonly InspectionWork _work;
    private readonly NgCarrierTransfer _transfer;
    private readonly NgShuttle _shuttle;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // Current loop destinations for display only; never resume them after STOP.
    private HeatSinkSlot? _activePcb;
    private BoltPoint? _activeBolt;

    public InspectionStation(
        InspectionWork work,
        NgCarrierTransfer transfer,
        NgShuttle shuttle,
        UnitSettings units,
        ICamera camera,
        ILightController light,
        LightingSettings lightingSettings,
        RecipeManager recipes)
    {
        _work = work;
        _transfer = transfer;
        _shuttle = shuttle;
        _units = units;
        _camera = camera;
        _light = light;
        _lightingSettings = lightingSettings;
        _recipes = recipes;
        _visionGate = new(1, 1);
        camera.LiveViewFailed += OnCameraLiveViewFailed;
        work.Changed += NotifyChanged;
        transfer.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    public InspectionStationState GetState(
        IReadOnlyList<BoltPoint> bolts,
        bool repeat = false,
        bool holdAtShuttle = false,
        bool live = true,
        bool? conveyorRunning = null,
        bool? mainConveyorRunning = null)
    {
        if (_work.CarrierSeatingRequested)
            return InspectionStationState.SeatingCarrier;
        switch (GetTransferState(repeat, holdAtShuttle, live, conveyorRunning))
        {
            case NgTransferState.PreparingTransfer or NgTransferState.PickingCarrier or NgTransferState.PlacingCarrier:
                return InspectionStationState.TransferringNgCarrier;
            case NgTransferState.WaitingForDestination:
                return InspectionStationState.WaitingForShuttleReady;
            case NgTransferState.HoldingAtDestination:
                return InspectionStationState.HoldingCarrierAtShuttle;
            default:
                var target = InspectionTarget;
                return GetNextInspectionState(target.Pcb, target.Bolt, live, mainConveyorRunning);
        }
    }

    public BoltPoint? GetActiveBolt(IReadOnlyList<BoltPoint> bolts, bool? mainConveyorRunning = null)
    {
        return _work.Enabled
            && _work.IsReadyToInspect(mainConveyorRunning)
            ? InspectionTarget.Bolt
            : null;
    }

    public HeatSinkSlot? GetActivePcb(IReadOnlyList<BoltPoint> bolts, bool? mainConveyorRunning = null)
    {
        return _work.Enabled && _work.IsReadyToInspect(mainConveyorRunning)
            ? InspectionTarget.Pcb
            : null;
    }

    public async Task RunAsync(
        IReadOnlyList<BoltPoint> bolts,
        CancellationToken cancellationToken = default,
        bool repeat = false,
        bool holdAtShuttle = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        BeginRun();
        try
        {
            if (_work.Enabled)
                _work.Restart(_work.CurrentJob);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (repeat && !_units.MainConveyor)
                    await PrepareRepeatAsync(cancellationToken);
                if (!_work.Enabled)
                {
                    var job = _work.CurrentJob;
                    if (_work.Station.CarrierSeated || _work.AtInspectionPosition)
                        _work.Complete(job);
                    if (!_units.NgCarrierTransfer && !_work.CarrierSeatingRequested)
                    {
                        TraceStep(InspectionStationState.Disabled, workId: job.Id,
                            waitingFor: _work.Completed ? "carrier transfer" : "carrier at station");
                        await WaitForChangeAsync(cancellationToken);
                        continue;
                    }
                }
                await ExecuteAsync(bolts, repeat, holdAtShuttle, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.ClearCarrierSeatingRequest();
            EndRun(cancellationToken);
        }
    }

    private async Task ExecuteAsync(
        IReadOnlyList<BoltPoint> bolts,
        bool repeat,
        bool holdAtShuttle,
        CancellationToken cancellationToken)
    {
        if (_work.CarrierSeatingRequested)
        {
            TraceStep(InspectionStationState.SeatingCarrier, workId: _work.CurrentJob.Id);
            await _transfer.SeatStationAsync(cancellationToken);
            _work.ClearCarrierSeatingRequest();
            return;
        }
        var transferState = GetTransferState(repeat, holdAtShuttle);
        if (transferState is not (NgTransferState.Idle or NgTransferState.Completed))
        {
            if (!await _transfer.ExecuteAsync(
                NgTransferDestination.Shuttle, transferState, cancellationToken, holdAtShuttle,
                allowEmpty: repeat && _transfer.IsEmptyRepeatAllowed))
                await WaitForChangeAsync(cancellationToken);

            return;
        }

        var nextTarget = InspectionTarget;
        var nextState = GetNextInspectionState(nextTarget.Pcb, nextTarget.Bolt);
        TraceStep(nextState, workId: _work.CurrentJob.Id,
            waitingFor: nextState is InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor
                ? "carrier, supports and clear pickup" : null);
        switch (nextState)
        {
            case InspectionStationState.ReturningToWaitingPosition:
                await MoveToWaitingPositionAsync(cancellationToken);
                return;
            case InspectionStationState.Disabled
                or InspectionStationState.Waiting
                or InspectionStationState.WaitingForConveyor
                or InspectionStationState.BarcodeTeachingRequired
                or InspectionStationState.FovTeachingRequired:
                await WaitForChangeAsync(cancellationToken);
                return;
        }

        var job = _work.CurrentJob;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckWorkPosition()
        {
            if (!_work.IsReadyToInspect())
            {
                operation.Cancel();
            }
        }

        _work.Changed += CheckWorkPosition;
        try
        {
            CheckWorkPosition();
            operation.Token.ThrowIfCancellationRequested();
            var targets = Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent).ToArray();
            foreach (var heatSink in targets)
            {
                if (!bolts.Any(bolt => bolt.HeatSink == heatSink))
                    throw new InvalidOperationException(
                        $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before inspection.");
            }
            _runTargets = targets;

            foreach (var pcb in targets)
            {
                operation.Token.ThrowIfCancellationRequested();
                _work.RequireCurrentJob(job);
                _activePcb = pcb;
                _activeBolt = null;
                NotifyChanged();
                await WaitForTeachingAsync(pcb, null, operation.Token);
                TraceStep(InspectionStationState.ReadingBarcode, $"{pcb.GetDescription()} / Data Matrix", job.Id);
                var assembly = _work.GetAssembly(job, pcb);
                var barcode = await ReadBarcodeAsync(pcb, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _work.RequireCurrentJob(job);
                assembly.PcbBarcode = barcode.Barcode;
                assembly.RecordInspectionCapture(barcode);

                foreach (var bolt in bolts.Where(bolt => bolt.HeatSink == pcb).OrderBy(bolt => bolt.Number))
                {
                    operation.Token.ThrowIfCancellationRequested();
                    _work.RequireCurrentJob(job);
                    _activeBolt = bolt;
                    NotifyChanged();
                    await WaitForTeachingAsync(pcb, bolt, operation.Token);
                    TraceStep(InspectionStationState.InspectingBolt, $"{pcb.GetDescription()} / Bolt {bolt.Number}", job.Id);
                    var capture = await InspectAsync(bolt, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    _work.RequireCurrentJob(job);
                    assembly.RecordBoltPresence(bolt.Number, capture.Success);
                    assembly.RecordInspectionCapture(capture);
                }
            }

            _activePcb = null;
            _activeBolt = null;
            NotifyChanged();
            TraceStep(InspectionStationState.CompletingInspection, workId: job.Id);
            await MoveToWaitingPositionAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            foreach (var heatSink in targets)
                _work.GetAssembly(job, heatSink).CompleteInspection();
            _work.Complete(job);
        }
        catch (OperationCanceledException) when (
            operation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Changed -= CheckWorkPosition;
            _runTargets = null;
            _activePcb = null;
            _activeBolt = null;
            NotifyChanged();
        }
    }

    private async Task WaitForTeachingAsync(HeatSinkSlot pcb, BoltPoint? bolt, CancellationToken cancellationToken)
    {
        while (bolt is null ? !HasBarcodeRegion(pcb) : !HasRegion(bolt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = WaitAtWaitingPosition(bolt is null
                ? InspectionStationState.BarcodeTeachingRequired
                : InspectionStationState.FovTeachingRequired, live: true);
            TraceStep(state, workId: _work.CurrentJob.Id);
            if (state == InspectionStationState.ReturningToWaitingPosition)
                await MoveToWaitingPositionAsync(cancellationToken);
            else
                await WaitForChangeAsync(cancellationToken);
        }
    }

    private NgTransferState GetTransferState(
        bool repeat,
        bool holdAtShuttle,
        bool live = true,
        bool? conveyorRunning = null)
    {
        if (!_units.NgCarrierTransfer)
            return NgTransferState.Idle;

        var canReceive = holdAtShuttle
            || _shuttle.IsReceiveAllowed(useConveyor: !repeat || _units.NgConveyor, conveyorRunning);
        return _transfer.GetState(
            NgTransferDestination.Shuttle,
            canPickUp: (repeat && _transfer.IsEmptyRepeatAllowed || _work.Station.CarrierSeated
                && _work.Completed
                && (repeat || _work.RouteToNg))
                && canReceive,
            canReceive: canReceive,
            holdAtDestination: holdAtShuttle,
            live: live,
            allowEmpty: repeat && _transfer.IsEmptyRepeatAllowed);
    }

    private InspectionStationState GetNextInspectionState(
        HeatSinkSlot? pcb,
        BoltPoint? bolt,
        bool live = true,
        bool? mainConveyorRunning = null)
    {
        var enabled = _work.Enabled;
        if (!enabled || !_work.IsReadyToInspect(mainConveyorRunning))
        {
            var waiting = !enabled ? InspectionStationState.Disabled
                : _work.IsWaitingForConveyor
                    ? InspectionStationState.WaitingForConveyor
                    : InspectionStationState.Waiting;
            return WaitAtWaitingPosition(waiting, live);
        }
        if (pcb is null)
            return InspectionStationState.CompletingInspection;
        if (bolt is null)
        {
            return HasBarcodeRegion(pcb.Value)
                ? InspectionStationState.ReadingBarcode
                : WaitAtWaitingPosition(InspectionStationState.BarcodeTeachingRequired, live);
        }
        return HasRegion(bolt)
            ? InspectionStationState.InspectingBolt
            : WaitAtWaitingPosition(InspectionStationState.FovTeachingRequired, live);
    }

    private InspectionStationState WaitAtWaitingPosition(InspectionStationState waiting, bool live)
    {
        if (_work.Enabled && _work.WaitingPosition is null)
            return InspectionStationState.BarcodeTeachingRequired;
        return _work.PickupClear && !_work.IsTransferAtWaitingPosition(live)
            ? InspectionStationState.ReturningToWaitingPosition
            : waiting;
    }

    private async Task MoveToWaitingPositionAsync(CancellationToken cancellationToken)
    {
        var position = _work.WaitingPosition
            ?? throw new InvalidOperationException("Record the inspection waiting position before moving.");
        if (!_transfer.IsAt(position))
            await _transfer.MoveToAsync(position, cancellationToken: cancellationToken);
    }

    private (HeatSinkSlot? Pcb, BoltPoint? Bolt) InspectionTarget
    {
        get
        {
            if (_runTargets is not null)
                return (_activePcb, _activeBolt);
            foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
            {
                if (_work.Station.IsHeatSinkPresent(pcb))
                    return (pcb, null);
            }
            return (null, null);
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
