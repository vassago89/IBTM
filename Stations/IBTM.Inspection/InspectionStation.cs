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

    public InspectionStation(
        InspectionWork work,
        NgCarrierTransfer transfer,
        NgShuttle shuttle,
        UnitSettings units,
        ICamera camera,
        ILightController light,
        InspectionGantrySettings gantrySettings,
        LightingSettings lightingSettings,
        RecipeManager recipes)
    {
        _work = work;
        _transfer = transfer;
        _shuttle = shuttle;
        _units = units;
        _camera = camera;
        _light = light;
        _gantrySettings = gantrySettings;
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
        switch (GetTransferState(repeat, holdAtShuttle, live, conveyorRunning))
        {
            case NgTransferState.PreparingTransfer or NgTransferState.PickingCarrier or NgTransferState.PlacingCarrier:
                return InspectionStationState.TransferringNgCarrier;
            case NgTransferState.WaitingForDestination:
                return InspectionStationState.WaitingForShuttleReady;
            case NgTransferState.HoldingAtDestination:
                return InspectionStationState.HoldingCarrierAtShuttle;
            default:
                return GetNextInspectionState(GetNextBolt(bolts), live, mainConveyorRunning);
        }
    }

    public BoltPoint? GetActiveBolt(IReadOnlyList<BoltPoint> bolts, bool? mainConveyorRunning = null)
    {
        return _work.Enabled
            && _work.IsReadyToInspect(mainConveyorRunning)
            && NextBarcode is null
            ? GetNextBolt(bolts)
            : null;
    }

    public HeatSinkSlot? GetActivePcb(IReadOnlyList<BoltPoint> bolts, bool? mainConveyorRunning = null)
    {
        return _work.Enabled && _work.IsReadyToInspect(mainConveyorRunning)
            ? NextBarcode ?? GetNextBolt(bolts)?.HeatSink
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
            while (!cancellationToken.IsCancellationRequested)
            {
                if (repeat && !_units.MainConveyor)
                    await PrepareRepeatAsync(cancellationToken);
                if (!_work.Enabled)
                {
                    var job = _work.CurrentJob;
                    if (_work.Station.CarrierSeated || _work.AtInspectionPosition)
                        _work.Complete(job);
                    if (!_units.NgCarrierTransfer)
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
            EndRun(cancellationToken);
        }
    }

    private async Task ExecuteAsync(
        IReadOnlyList<BoltPoint> bolts,
        bool repeat,
        bool holdAtShuttle,
        CancellationToken cancellationToken)
    {
        var transferState = GetTransferState(repeat, holdAtShuttle);
        if (transferState is not (NgTransferState.Idle or NgTransferState.Completed))
        {
            if (!await _transfer.ExecuteAsync(
                NgTransferDestination.Shuttle, transferState, cancellationToken, holdAtShuttle,
                allowEmpty: repeat && _transfer.IsEmptyRepeatAllowed))
                await WaitForChangeAsync(cancellationToken);

            return;
        }

        var nextState = GetNextInspectionState(GetNextBolt(bolts));
        TraceStep(nextState, workId: _work.CurrentJob.Id,
            waitingFor: nextState is InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor
                ? "carrier, supports and clear pickup" : null);
        switch (nextState)
        {
            case InspectionStationState.ReturningToNgPickup:
                await _transfer.MoveToCarrierAsync(NgTransferDestination.Station, cancellationToken);
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

            while (!operation.IsCancellationRequested)
            {
                var bolt = GetNextBolt(bolts);
                var inspectionState = GetNextInspectionState(bolt);
                TraceStep(inspectionState, bolt?.ToString() ?? NextBarcode?.ToString(), job.Id);
                switch (inspectionState)
                {
                    case InspectionStationState.ReadingBarcode:
                        var barcodeAssembly = _work.GetAssembly(job, NextBarcode!.Value);
                        await MoveToBarcodeAsync(barcodeAssembly.HeatSink, operation.Token);
                        var barcode = await ReadBarcodeAsync(barcodeAssembly.HeatSink, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        _work.RequireCurrentJob(job);
                        barcodeAssembly.PcbBarcode = barcode;
                        NotifyChanged();
                        break;
                    case InspectionStationState.InspectingBolt:
                        var assembly = _work.GetAssembly(job, bolt!.HeatSink);
                        await MoveToAsync(bolt, operation.Token);
                        var present = await InspectAsync(bolt, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        _work.RequireCurrentJob(job);
                        assembly.RecordBoltPresence(bolt.Number, present);
                        NotifyChanged();
                        break;
                    case InspectionStationState.CompletingInspection:
                        await _transfer.MoveToCarrierAsync(NgTransferDestination.Station, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        foreach (var heatSink in targets)
                        {
                            _work.GetAssembly(job, heatSink).CompleteInspection();
                        }

                        _work.Complete(job);
                        return;
                    default:
                        return;
                }
            }
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

    private InspectionStationState GetNextInspectionState(BoltPoint? bolt, bool live = true, bool? mainConveyorRunning = null)
    {
        var enabled = _work.Enabled;
        switch (true)
        {
            case true when !enabled || !_work.IsReadyToInspect(mainConveyorRunning):
                {
                    var waiting = !enabled ? InspectionStationState.Disabled
                        : _work.IsWaitingForConveyor
                            ? InspectionStationState.WaitingForConveyor
                            : InspectionStationState.Waiting;
                    return WaitAtPickup(waiting, live);
                }
            case true when NextBarcode is { } pcb:
                if (!HasBarcodeRegion(pcb))
                    return WaitAtPickup(InspectionStationState.BarcodeTeachingRequired, live);
                return InspectionStationState.ReadingBarcode;
            case true when bolt is null:
                return InspectionStationState.CompletingInspection;
            case true when !HasPosition(bolt):
                return WaitAtPickup(InspectionStationState.FovTeachingRequired, live);
            default:
                return InspectionStationState.InspectingBolt;
        }
    }

    private InspectionStationState WaitAtPickup(InspectionStationState waiting, bool live)
    {
        return _work.PickupClear && !_work.IsTransferAtWaitingPosition(live)
            ? InspectionStationState.ReturningToNgPickup
            : waiting;
    }

    private HeatSinkSlot? NextBarcode
    {
        get
        {
            return Enum.GetValues<HeatSinkSlot>()
                .Where(pcb => _runTargets?.Contains(pcb) ?? _work.Station.IsHeatSinkPresent(pcb))
                .Where(pcb =>
                    _work.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb)?.PcbBarcode is null)
                .Select(pcb => (HeatSinkSlot?)pcb)
                .FirstOrDefault();
        }
    }

    private BoltPoint? GetNextBolt(IReadOnlyList<BoltPoint> bolts)
    {
        var targets = _runTargets;
        return bolts.Where(
            bolt => targets?.Contains(bolt.HeatSink) ?? _work.Station.IsHeatSinkPresent(bolt.HeatSink))
            .OrderBy(bolt => bolt.HeatSink)
            .ThenBy(bolt => bolt.Number)
            .FirstOrDefault(
                bolt =>
                    !_work.Assemblies.Any(
                        assembly =>
                            assembly.HeatSink == bolt.HeatSink
                                && assembly.BoltPresenceResults.ContainsKey(bolt.Number)));
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
