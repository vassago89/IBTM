using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.NgConveyor;

namespace IBTM.Inspection;

public sealed class InspectionStation : AutoUnit
{
    private readonly InspectionWork _work;
    private readonly BoltInspector _inspector;
    private readonly NgCarrierMove _move;
    private readonly NgShuttle _shuttle;
    private readonly Func<bool> _isTransferEnabled;
    private readonly Func<bool> _isConveyorEnabled;
    private HeatSinkSlot[]? _runTargets;

    public InspectionStation(
        InspectionWork work,
        BoltInspector inspector,
        NgCarrierMove move,
        NgShuttle shuttle,
        Func<bool> isTransferEnabled,
        Func<bool> isConveyorEnabled)
    {
        _work = work;
        _inspector = inspector;
        _move = move;
        _shuttle = shuttle;
        _isTransferEnabled = isTransferEnabled;
        _isConveyorEnabled = isConveyorEnabled;
        move.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    public InspectionStationState GetState(
        IReadOnlyList<BoltTarget> bolts,
        bool repeat = false,
        bool holdAtShuttle = false,
        bool live = true,
        bool? conveyorRunning = null)
    {
        return GetTransferDisplayState(GetTransferState(repeat, holdAtShuttle, live, conveyorRunning))
            ?? GetNextInspectionState(GetNextBolt(bolts), live);
    }

    public BoltTarget? GetActiveBolt(IReadOnlyList<BoltTarget> bolts)
    {
        return _work.Enabled
            && _work.State == InspectionWorkState.ReadyToInspect
            && GetNextBarcode() is null
            ? GetNextBolt(bolts)
            : null;
    }

    public HeatSinkSlot? GetActivePcb(IReadOnlyList<BoltTarget> bolts)
    {
        return _work.Enabled && _work.State == InspectionWorkState.ReadyToInspect
            ? GetNextBarcode() ?? GetNextBolt(bolts)?.HeatSink
            : null;
    }

    public async Task RunAsync(
        IReadOnlyList<BoltTarget> bolts,
        CancellationToken cancellationToken = default,
        bool repeat = false,
        bool holdAtShuttle = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        await RunLoopAsync(token => ExecuteAsync(bolts, repeat, holdAtShuttle, token), cancellationToken);
    }

    private async Task ExecuteAsync(
        IReadOnlyList<BoltTarget> bolts,
        bool repeat,
        bool holdAtShuttle,
        CancellationToken cancellationToken)
    {
        var transferState = GetTransferState(repeat, holdAtShuttle);
        if (GetTransferDisplayState(transferState) is not null)
        {
            var transfer = _move.ExecuteAsync(
                NgTransferDestination.Shuttle,
                transferState,
                cancellationToken);
            if (transfer is null)
            {
                await WaitForChangeAsync(cancellationToken);
            }
            else
            {
                await transfer;
            }

            return;
        }

        var inspectionState = GetNextInspectionState(GetNextBolt(bolts));
        TraceStep(inspectionState, workId: _work.CurrentJob.Id,
            waitingFor: inspectionState is InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor
                ? _work.State.ToString() : null);
        if (inspectionState == InspectionStationState.ReturningToNgPickup)
        {
            await _move.MoveToCarrierAsync(NgTransferDestination.Station, cancellationToken);
            return;
        }
        if (inspectionState is InspectionStationState.Waiting
            or InspectionStationState.WaitingForConveyor
            or InspectionStationState.BarcodeTeachingRequired
            or InspectionStationState.FovTeachingRequired)
        {
            await WaitForChangeAsync(cancellationToken);
            return;
        }

        await ExecuteInspectionAsync(bolts, cancellationToken);
    }

    private NgTransferState GetTransferState(
        bool repeat,
        bool holdAtShuttle,
        bool live = true,
        bool? conveyorRunning = null)
    {
        if (!_isTransferEnabled())
            return NgTransferState.Idle;

        var canReceive = holdAtShuttle
            || _shuttle.CanReceive(useConveyor: !repeat || _isConveyorEnabled(), conveyorRunning);
        return _move.GetState(
            NgTransferDestination.Shuttle,
            canPickUp: _work.Station.CarrierSeated
                && _work.Completed
                && (repeat || _work.RouteToNg)
                && canReceive,
            canReceive: canReceive,
            holdAtDestination: holdAtShuttle,
            live: live);
    }

    // Inspection's display enum describes the same shared transfer states.
    private static InspectionStationState? GetTransferDisplayState(NgTransferState state)
    {
        return state switch
        {
            NgTransferState.MovingToCarrier => InspectionStationState.MovingTransferToCarrier,
            NgTransferState.LoweringToCarrier => InspectionStationState.LoweringTransferAtCarrier,
            NgTransferState.Closing => InspectionStationState.ClosingTransferGripper,
            NgTransferState.WaitingForGrip => InspectionStationState.WaitingForCarrierGrip,
            NgTransferState.Raising => InspectionStationState.RaisingCarrierTransfer,
            NgTransferState.MovingToDestination => InspectionStationState.MovingTransferToShuttle,
            NgTransferState.StationNotReady
                or NgTransferState.ShuttleNotReady
                or NgTransferState.WaitingForDestination
                => InspectionStationState.WaitingForShuttleReady,
            NgTransferState.LoweringAtDestination => InspectionStationState.LoweringTransferAtShuttle,
            NgTransferState.Opening => InspectionStationState.OpeningTransferGripper,
            NgTransferState.WaitingForPlacement => InspectionStationState.WaitingForShuttleCarrier,
            NgTransferState.HoldingAtDestination => InspectionStationState.HoldingCarrierAtShuttle,
            _ => null,
        };
    }

    private async Task ExecuteInspectionAsync(
        IReadOnlyList<BoltTarget> bolts,
        CancellationToken cancellationToken)
    {
        var job = _work.CurrentJob;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckWorkPosition()
        {
            if (_work.State != InspectionWorkState.ReadyToInspect)
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
                TraceStep(inspectionState, bolt?.ToString() ?? GetNextBarcode()?.ToString(), job.Id);
                switch (inspectionState)
                {
                    case InspectionStationState.MovingToBarcode:
                        await _inspector.MoveToBarcodeAsync(GetNextBarcode()!.Value, operation.Token);
                        break;
                    case InspectionStationState.ReadingBarcode:
                        var barcodeAssembly = _work.GetAssembly(job, GetNextBarcode()!.Value);
                        var barcode = await _inspector.ReadBarcodeAsync(barcodeAssembly.HeatSink, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        _work.RequireCurrentJob(job);
                        barcodeAssembly.RecordBarcode(barcode);
                        NotifyChanged();
                        break;
                    case InspectionStationState.MovingToBolt:
                        await _inspector.MoveToAsync(bolt!, operation.Token);
                        break;
                    case InspectionStationState.InspectingBolt:
                        var assembly = _work.GetAssembly(job, bolt!.HeatSink);
                        var present = await _inspector.InspectAsync(bolt, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        _work.RequireCurrentJob(job);
                        assembly.RecordBoltPresence(bolt.Number, present);
                        NotifyChanged();
                        break;
                    case InspectionStationState.ReturningToNgPickup:
                        await _move.MoveToCarrierAsync(NgTransferDestination.Station, operation.Token);
                        break;
                    case InspectionStationState.CompletingInspection:
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

    private InspectionStationState GetNextInspectionState(BoltTarget? bolt, bool live = true)
    {
        var enabled = _work.Enabled;
        var workState = _work.State;
        if (!enabled || workState != InspectionWorkState.ReadyToInspect)
        {
            var waiting = enabled && workState == InspectionWorkState.WaitingForConveyor
                ? InspectionStationState.WaitingForConveyor
                : InspectionStationState.Waiting;
            return WaitAtPickup(waiting, live);
        }

        if (GetNextBarcode() is { } pcb)
        {
            if (!_inspector.HasBarcodeRegion(pcb))
                return WaitAtPickup(InspectionStationState.BarcodeTeachingRequired, live);
            return _inspector.IsAtBarcode(pcb, live)
                ? InspectionStationState.ReadingBarcode
                : InspectionStationState.MovingToBarcode;
        }

        if (bolt is null)
        {
            return _work.IsTransferAtWaitingPosition(live)
                ? InspectionStationState.CompletingInspection
                : InspectionStationState.ReturningToNgPickup;
        }

        if (!_inspector.HasPosition(bolt))
            return WaitAtPickup(InspectionStationState.FovTeachingRequired, live);
        return _inspector.IsAt(bolt, live)
            ? InspectionStationState.InspectingBolt
            : InspectionStationState.MovingToBolt;
    }

    private InspectionStationState WaitAtPickup(InspectionStationState waiting, bool live)
    {
        return _work.PickupClear && !_work.IsTransferAtWaitingPosition(live)
            ? InspectionStationState.ReturningToNgPickup
            : waiting;
    }

    private HeatSinkSlot? GetNextBarcode()
    {
        return Enum.GetValues<HeatSinkSlot>()
            .Where(pcb => _runTargets?.Contains(pcb) ?? _work.Station.IsHeatSinkPresent(pcb))
            .Where(pcb =>
                _work.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb)?.PcbBarcode is null)
            .Select(pcb => (HeatSinkSlot?)pcb)
            .FirstOrDefault();
    }

    private BoltTarget? GetNextBolt(IReadOnlyList<BoltTarget> bolts)
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
