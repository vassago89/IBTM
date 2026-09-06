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
    private readonly bool _inspectionEnabled;
    private readonly bool _transferEnabled;
    private HeatSinkSlot[]? _runTargets;

    public InspectionStation(
        InspectionWork work,
        BoltInspector inspector,
        NgCarrierTransfer transfer,
        InspectionGantry gantry,
        NgCarrierTransferSettings transferSettings,
        NgShuttle shuttle,
        bool inspectionEnabled,
        bool transferEnabled)
    {
        _work = work;
        _inspector = inspector;
        _transfer = transfer;
        _gantry = gantry;
        _transferSettings = transferSettings;
        _shuttle = shuttle;
        _inspectionEnabled = inspectionEnabled;
        _transferEnabled = transferEnabled;
        work.Changed += NotifyChanged;
        transfer.Changed += NotifyChanged;
        shuttle.Changed += NotifyChanged;
    }

    public event Action? Changed;

    public InspectionStationState State(
        IReadOnlyList<BoltPoint> bolts) =>
        TransferState() ?? NextInspectionState(NextBolt(bolts));

    public BoltPoint? ActiveBolt(IReadOnlyList<BoltPoint> bolts) =>
        _inspectionEnabled
        && _work.State == InspectionWorkState.ReadyToInspect
            ? NextBolt(bolts)
            : null;

    public async Task RunAsync(
        IReadOnlyList<BoltPoint> bolts,
        CancellationToken cancellationToken = default)
    {
        if (_inspectionEnabled && _work.CarrierPresent && !_work.Completed)
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
        && (!_inspectionEnabled || _work.HasNg)
        && _shuttle.CanReceive;

    private bool TransferAtCarrier =>
        _gantry.IsAt(_transferSettings.CarrierPickupPosition);
    private bool TransferAtShuttle =>
        _gantry.IsAt(_transferSettings.ShuttlePlacePosition);

    private InspectionStationState? TransferState()
    {
        if (!_transferEnabled)
        {
            return null;
        }

        if (TransferAtShuttle
            && _transfer.Lift == NgTransferLiftState.Down
            && _transfer.Gripper == NgTransferGripperState.Open)
        {
            return _shuttle.Feedback.CarrierDetected
                ? InspectionStationState.RaisingCarrierTransfer
                : InspectionStationState.WaitingForShuttleCarrier;
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
        IReadOnlyList<BoltPoint> bolts,
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
                    case InspectionStationState.MovingToBolt:
                        await _inspector.MoveToAsync(bolt!, operation.Token);
                        break;
                    case InspectionStationState.InspectingBolt:
                        var assembly = _work.Assembly(bolt!.HeatSink);
                        var present = await _inspector.InspectAsync(
                            operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        assembly.RecordBoltPresence(
                            bolt.Number,
                            present);
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

    private InspectionStationState NextInspectionState(BoltPoint? bolt)
    {
        if (!_inspectionEnabled
            || _work.State != InspectionWorkState.ReadyToInspect)
        {
            return InspectionStationState.Waiting;
        }

        if (bolt is null)
        {
            return InspectionStationState.CompletingInspection;
        }

        return _inspector.IsAt(bolt)
            ? InspectionStationState.InspectingBolt
            : InspectionStationState.MovingToBolt;
    }

    private BoltPoint? NextBolt(IReadOnlyList<BoltPoint> bolts)
    {
        var targets = _runTargets;
        return bolts
            .Where(bolt => targets?.Contains(bolt.HeatSink)
                ?? _work.HeatSinkPresent(bolt.HeatSink))
            .OrderBy(bolt => bolt.Number)
            .FirstOrDefault(bolt => !_work.Assemblies.Any(assembly =>
                assembly.HeatSink == bolt.HeatSink
                && assembly.BoltPresenceResults.ContainsKey(bolt.Number)));
    }

    private void NotifyChanged() => Changed?.Invoke();
}
