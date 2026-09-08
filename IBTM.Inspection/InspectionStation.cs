using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.NgConveyor;

namespace IBTM.Inspection;

public sealed class InspectionStation
{
    private readonly InspectionWork _work;
    private readonly BoltInspector _inspector;
    private readonly NgCarrierTransfer _transfer;
    private readonly InspectionGantry _gantry;
    private readonly NgCarrierTransferSettings _transferSettings;
    private readonly NgShuttle _shuttle;
    private readonly Func<bool> _isTransferEnabled;
    private HeatSinkSlot[]? _runTargets;

    public InspectionStation(
        InspectionWork work,
        BoltInspector inspector,
        NgCarrierTransfer transfer,
        InspectionGantry gantry,
        NgCarrierTransferSettings transferSettings,
        NgShuttle shuttle,
        Func<bool> isTransferEnabled)
    {
        _work = work;
        _inspector = inspector;
        _transfer = transfer;
        _gantry = gantry;
        _transferSettings = transferSettings;
        _shuttle = shuttle;
        _isTransferEnabled = isTransferEnabled;
        work.Changed += NotifyChanged;
        transfer.Changed += NotifyChanged;
        shuttle.Changed += NotifyChanged;
    }

    public event Action? Changed;

    public InspectionStationState State(
        IReadOnlyList<BoltTarget> bolts) =>
        TransferState() ?? NextInspectionState(NextBolt(bolts));

    public BoltTarget? ActiveBolt(IReadOnlyList<BoltTarget> bolts) =>
        _work.Enabled
        && _work.State == InspectionWorkState.ReadyToInspect
        && NextBarcode() is null
            ? NextBolt(bolts)
            : null;

    public HeatSinkSlot? ActivePcb(IReadOnlyList<BoltTarget> bolts) =>
        _work.Enabled && _work.State == InspectionWorkState.ReadyToInspect
            ? NextBarcode() ?? NextBolt(bolts)?.HeatSink
            : null;

    public async Task RunAsync(
        IReadOnlyList<BoltTarget> bolts,
        CancellationToken cancellationToken = default)
    {
        if (_work.Enabled && _work.CarrierPresent && !_work.Completed)
        {
            _work.RestartInspection();
        }

        var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                switch (State(bolts))
                {
                    case InspectionStationState.MovingToBarcode:
                    case InspectionStationState.ReadingBarcode:
                    case InspectionStationState.MovingToBolt:
                    case InspectionStationState.InspectingBolt:
                    case InspectionStationState.CompletingInspection:
                        await ExecuteInspectionAsync(
                            bolts,
                            cancellationToken);
                        break;
                    case InspectionStationState.MovingTransferToCarrier:
                        await _gantry.MoveToAsync(
                            _transferSettings.CarrierPickupPosition,
                            _transferSettings.Speed,
                            cancellationToken);
                        break;
                    case InspectionStationState.LoweringTransferAtCarrier:
                    case InspectionStationState.LoweringTransferAtShuttle:
                        await _transfer.SetLiftDownAsync(
                            true,
                            cancellationToken);
                        break;
                    case InspectionStationState.ClosingTransferGripper:
                        await _transfer.SetGripperClosedAsync(
                            true,
                            cancellationToken);
                        break;
                    case InspectionStationState.WaitingForCarrierGrip:
                        await _transfer.WaitForCarrierGripAsync(
                            cancellationToken);
                        break;
                    case InspectionStationState.RaisingCarrierTransfer:
                        await _transfer.SetLiftDownAsync(
                            false,
                            cancellationToken);
                        break;
                    case InspectionStationState.MovingTransferToShuttle:
                        await _gantry.MoveToAsync(
                            _transferSettings.ShuttlePlacePosition,
                            _transferSettings.Speed,
                            cancellationToken);
                        break;
                    case InspectionStationState.OpeningTransferGripper:
                        await _transfer.SetGripperClosedAsync(
                            false,
                            cancellationToken);
                        break;
                    case InspectionStationState.WaitingForShuttleCarrier:
                        await _shuttle.WaitForCarrierAsync(
                            true,
                            cancellationToken);
                        break;
                    default:
                        await stateChanged.WaitAsync(cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Changed -= OnStateChanged;
        }
    }

    private bool CarrierReadyForNg =>
        _work.CarrierSeated
        && _work.Completed
        && _work.RouteToNg
        && _shuttle.CanReceive;

    private bool TransferAtCarrier =>
        _gantry.IsAt(_transferSettings.CarrierPickupPosition);
    private bool TransferAtShuttle =>
        _gantry.IsAt(_transferSettings.ShuttlePlacePosition);

    private InspectionStationState? TransferState()
    {
        if (!_isTransferEnabled())
        {
            return null;
        }

        if (TransferAtShuttle)
        {
            if (_transfer.Gripper == NgTransferGripperState.Open)
            {
                if (_transfer.Lift != NgTransferLiftState.Up)
                {
                    return _shuttle.Feedback.CarrierDetected
                        ? InspectionStationState.RaisingCarrierTransfer
                        : InspectionStationState.WaitingForShuttleCarrier;
                }

                if (_transfer.CarrierDetected || _shuttle.Feedback.CarrierDetected)
                {
                    return null;
                }
            }

            if (_transfer.Lift == NgTransferLiftState.Down
                && _shuttle.Feedback.CarrierDetected)
            {
                return InspectionStationState.OpeningTransferGripper;
            }
        }

        if (_transfer.CarrierDetected)
        {
            if (TransferAtShuttle
                && _transfer.Lift == NgTransferLiftState.Down)
            {
                return InspectionStationState.OpeningTransferGripper;
            }

            if (_transfer.Gripper != NgTransferGripperState.Closed)
            {
                return InspectionStationState.ClosingTransferGripper;
            }

            if (TransferAtShuttle)
            {
                if (!_shuttle.CanReceive)
                {
                    return InspectionStationState.WaitingForShuttleReady;
                }

                return InspectionStationState.LoweringTransferAtShuttle;
            }

            return _transfer.Lift == NgTransferLiftState.Up
                ? InspectionStationState.MovingTransferToShuttle
                : InspectionStationState.RaisingCarrierTransfer;
        }

        if (_shuttle.Feedback.CarrierDetected)
        {
            return _transfer.Lift == NgTransferLiftState.Up
                ? null
                : InspectionStationState.RaisingCarrierTransfer;
        }

        if (!CarrierReadyForNg)
        {
            if (_transfer.Lift != NgTransferLiftState.Up)
            {
                return InspectionStationState.RaisingCarrierTransfer;
            }

            return _transfer.Gripper == NgTransferGripperState.Open
                ? null
                : InspectionStationState.OpeningTransferGripper;
        }

        if (!TransferAtCarrier)
        {
            if (_transfer.Lift != NgTransferLiftState.Up)
            {
                return InspectionStationState.RaisingCarrierTransfer;
            }

            return _transfer.Gripper == NgTransferGripperState.Open
                ? InspectionStationState.MovingTransferToCarrier
                : InspectionStationState.OpeningTransferGripper;
        }

        if (_transfer.Lift != NgTransferLiftState.Down)
        {
            return InspectionStationState.LoweringTransferAtCarrier;
        }

        return _transfer.Gripper == NgTransferGripperState.Closed
            ? InspectionStationState.WaitingForCarrierGrip
            : InspectionStationState.ClosingTransferGripper;
    }

    private async Task ExecuteInspectionAsync(
        IReadOnlyList<BoltTarget> bolts,
        CancellationToken cancellationToken)
    {
        using var operation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        void CheckWorkPosition()
        {
            if (!_work.CarrierSeated || !_transfer.IsClear)
            {
                operation.Cancel();
            }
        }

        _work.Changed += CheckWorkPosition;
        try
        {
            CheckWorkPosition();
            operation.Token.ThrowIfCancellationRequested();
            var targets = Enum.GetValues<HeatSinkSlot>()
                .Where(_work.HeatSinkPresent)
                .ToArray();
            _runTargets = targets;

            while (!operation.IsCancellationRequested)
            {
                var bolt = NextBolt(bolts);
                switch (NextInspectionState(bolt))
                {
                    case InspectionStationState.MovingToBarcode:
                        await _inspector.MoveToBarcodeAsync(NextBarcode()!.Value, operation.Token);
                        break;
                    case InspectionStationState.ReadingBarcode:
                        var pcb = NextBarcode()!.Value;
                        var barcode = await _inspector.ReadBarcodeAsync(pcb, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        _work.Assembly(pcb).RecordBarcode(barcode);
                        NotifyChanged();
                        break;
                    case InspectionStationState.MovingToBolt:
                        await _inspector.MoveToAsync(bolt!, operation.Token);
                        break;
                    case InspectionStationState.InspectingBolt:
                        var assembly = _work.Assembly(bolt!.HeatSink);
                        var present = await _inspector.InspectAsync(
                            bolt,
                            operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        assembly.RecordBoltPresence(
                            bolt.Number,
                            present);
                        NotifyChanged();
                        break;
                    case InspectionStationState.CompletingInspection:
                        operation.Token.ThrowIfCancellationRequested();
                        foreach (var heatSink in targets)
                        {
                            _work.Assembly(heatSink).CompleteInspection();
                        }

                        _work.Complete();
                        return;
                    default:
                        return;
                }
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Changed -= CheckWorkPosition;
            _runTargets = null;
        }
    }

    private InspectionStationState NextInspectionState(BoltTarget? bolt)
    {
        if (!_work.Enabled
            || _work.State != InspectionWorkState.ReadyToInspect)
        {
            return InspectionStationState.Waiting;
        }

        if (NextBarcode() is { } pcb)
        {
            if (!_inspector.HasBarcodeRegion(pcb)) return InspectionStationState.BarcodeTeachingRequired;
            return _inspector.IsAtBarcode(pcb)
                ? InspectionStationState.ReadingBarcode
                : InspectionStationState.MovingToBarcode;
        }

        if (bolt is null)
        {
            return InspectionStationState.CompletingInspection;
        }

        return _inspector.IsAt(bolt)
            ? InspectionStationState.InspectingBolt
            : InspectionStationState.MovingToBolt;
    }

    private HeatSinkSlot? NextBarcode() => Enum.GetValues<HeatSinkSlot>()
        .Where(pcb => _runTargets?.Contains(pcb) ?? _work.HeatSinkPresent(pcb))
        .Where(pcb => _work.Assembly(pcb).PcbBarcode is null)
        .Select(pcb => (HeatSinkSlot?)pcb).FirstOrDefault();

    private BoltTarget? NextBolt(IReadOnlyList<BoltTarget> bolts)
    {
        var targets = _runTargets;
        return bolts
            .Where(bolt => targets?.Contains(bolt.HeatSink)
                ?? _work.HeatSinkPresent(bolt.HeatSink))
            .OrderBy(bolt => bolt.HeatSink).ThenBy(bolt => bolt.Number)
            .FirstOrDefault(bolt => !_work.Assemblies.Any(assembly =>
                assembly.HeatSink == bolt.HeatSink
                && assembly.BoltPresenceResults.ContainsKey(bolt.Number)));
    }

    private void NotifyChanged() => Changed?.Invoke();
}
