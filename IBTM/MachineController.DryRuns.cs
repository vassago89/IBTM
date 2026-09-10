using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;

namespace IBTM;

public sealed partial class MachineController
{
    private bool PcbReturnNeedsCarrier
    {
        get
        {
            return _pcbReturn.State == PcbReturnState.WaitingForCarrier;
        }
    }

    private bool IsDryRunReady(DryRunTarget target)
    {
        return target switch
        {
            DryRunTarget.NgTransfer => _units.NgCarrierTransfer,
            DryRunTarget.BoltRoute => _units.BoltFastening && _boltRoute.Ready,
            DryRunTarget.PcbReturn
                => _units.PcbSupply
                    && _units.PcbPlacement
                    && (!PcbReturnNeedsCarrier
                        || _units.MainConveyor && MainConveyorPathClear),
            DryRunTarget.PcbRoundTrip => _units.PcbSupply && _units.PcbPlacement,
            DryRunTarget.NgConveyor
                => _units.NgConveyor && _units.NgShuttle && _ngConveyorDryRun.Ready,
            DryRunTarget.Inspection => _units.Inspection && _inspectionDryRun.Ready,
            DryRunTarget.MainConveyor => _units.MainConveyor && MainConveyorPathClear,
            _ => false,
        };
    }

    internal async Task RunDryRunAsync(
        DryRunTarget target,
        CancellationToken cancellationToken,
        HeatSinkSlot pcb = HeatSinkSlot.HeatSink1)
    {
        (MachineAlarm Alarm, Func<CancellationToken, Task> Run) operation = target switch
        {
            DryRunTarget.MainConveyor
                => (MachineAlarm.MainConveyor,
                    token => RunConveyorDryRunAsync(_mainConveyorDryRun.RunAsync, token)),
            DryRunTarget.BoltRoute => (MachineAlarm.BoltFastening, _boltRoute.RunAsync),
            DryRunTarget.Inspection => (MachineAlarm.Inspection, _inspectionDryRun.RunAsync),
            DryRunTarget.PcbReturn
                => (MachineAlarm.PcbPlacement, token => RunPcbReturnAsync(pcb, token)),
            DryRunTarget.PcbRoundTrip
                => (MachineAlarm.PcbPlacement, token => _pcbDryRun.RunAsync(pcb, token)),
            DryRunTarget.NgConveyor => (MachineAlarm.NgConveyor, _ngConveyorDryRun.RunAsync),
            DryRunTarget.NgTransfer => (MachineAlarm.NgCarrierTransfer, _ngTransferDryRun.RunAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        try
        {
            await RunManualAsync(
                operation.Run,
                operation.Alarm,
                () => _state.ManualControlsEnabled && IsDryRunReady(target),
                cancellationToken,
                canContinue: () => _io.IsReady
                    && _state.ManualMode
                    && _state.SafetyReady
                    && !_state.IsError);
        }
        catch (Exception exception)
        {
            if (!_state.IsError)
                _state.SetError(operation.Alarm, exception);
            Stop();
        }
    }

    private async Task RunPcbReturnAsync(HeatSinkSlot heatSink, CancellationToken cancellationToken)
    {
        if (PcbReturnNeedsCarrier)
        {
            if (!await RunConveyorDryRunAsync(
                _mainConveyorDryRun.ReturnToStation1Async,
                cancellationToken)
                || !_mainConveyorDryRun.AtStation1)
                return;
            cancellationToken.ThrowIfCancellationRequested();
        }

        await _pcbReturn.RunAsync(heatSink, cancellationToken);
    }

    private async Task<bool> RunConveyorDryRunAsync(
        Func<CancellationToken, Task> run,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPath()
        {
            if (!MainConveyorPathClear)
                operation.Cancel();
        }

        _state.Changed += CheckPath;
        _placementHandler.Changed += CheckPath;
        _fasteningGantry.Changed += CheckPath;
        _ngTransfer.Changed += CheckPath;
        try
        {
            CheckPath();
            await run(operation.Token);
            return !operation.IsCancellationRequested;
        }
        finally
        {
            _state.Changed -= CheckPath;
            _placementHandler.Changed -= CheckPath;
            _fasteningGantry.Changed -= CheckPath;
            _ngTransfer.Changed -= CheckPath;
        }
    }

    private DryRunDisplay ReadDryRunDisplay(DryRunTarget target, OutputBlockReason conveyorPathBlock)
    {
        DryRunDisplay display;
        switch (target)
        {
            case DryRunTarget.NgTransfer:
                display = new(
                    _units.NgCarrierTransfer && _inspectionGantry.Motion.XyHomed
                        ? _ngTransferDryRun.State
                        : NgTransferState.Unavailable,
                    _ngTransferDryRun.Destination,
                    _ngTransferDryRun.CompletedTransfers);
                break;
            case DryRunTarget.Inspection:
                display = new(
                    _units.Inspection && _inspectionGantry.Motion.XyHomed
                        ? _inspectionDryRun.State
                        : InspectionDryRunState.Unavailable,
                    _inspectionDryRun.Direction,
                    _inspectionDryRun.CompletedPasses)
                {
                    Pcb = _inspectionDryRun.ActivePcb,
                    Bolt = _inspectionDryRun.ActiveBolt,
                    LastBarcode = _inspectionDryRun.LastBarcode,
                    LastBoltPresent = _inspectionDryRun.LastBoltPresent,
                };
                break;
            case DryRunTarget.MainConveyor:
                display = new(
                    conveyorPathBlock == OutputBlockReason.None
                        ? _mainConveyorDryRun.State
                        : MainConveyorDryRunState.Unavailable,
                    _mainConveyorDryRun.Destination,
                    _mainConveyorDryRun.CompletedPasses);
                break;
            case DryRunTarget.PcbReturn:
                var returningCarrier = PcbReturnNeedsCarrier && _mainConveyorDryRun.ReturningToStation1;
                display = new(
                    returningCarrier ? _mainConveyorDryRun.State : _pcbReturn.State,
                    returningCarrier ? _mainConveyorDryRun.Destination : _pcbReturn.Destination,
                    _pcbReturn.CompletedReturns)
                {
                    Pcb = _pcbReturn.HeatSink,
                };
                break;
            case DryRunTarget.PcbRoundTrip:
                display = new(_pcbDryRun.State, _pcbDryRun.Direction, _pcbDryRun.CompletedCycles)
                {
                    Pcb = _pcbDryRun.Direction == PcbDryRunDirection.Ready ? null : _pcbDryRun.HeatSink,
                };
                break;
            case DryRunTarget.NgConveyor:
                display = new(
                    _ngConveyorDryRun.State,
                    _ngConveyorDryRun.Destination,
                    _ngConveyorDryRun.CompletedPasses);
                break;
            case DryRunTarget.BoltRoute:
                var bolt = _boltRoute.ActiveBolt;
                display = new(
                    _units.BoltFastening && _fasteningGantry.Motion.XyHomed
                        ? _boltRoute.State
                        : BoltRouteState.Unavailable,
                    _boltRoute.Direction,
                    _boltRoute.CompletedPasses)
                {
                    Pcb = bolt?.HeatSink,
                    Bolt = bolt?.Number,
                    Head = bolt?.Head,
                    Pass = _boltRoute.ActivePass,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        return display with { Ready = IsDryRunReady(target) };
    }
}
