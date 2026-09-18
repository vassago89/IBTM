using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor
{
    public MainConveyorState State
    {
        get
        {
            return ReadState(RunCommandOn);
        }
    }

    public MainConveyorState ReadState(bool runCommandOn, bool live = true)
    {
        var executingTransfer = _executingTransfer;
        if (executingTransfer != MainConveyorState.Idle)
            return executingTransfer;
        if (runCommandOn)
            return MainConveyorState.Running;

        if (_boltFasteningWork.CarrierPresent && !_boltFasteningWork.CarrierSeated)
        {
            return MainConveyorState.SeatingBoltFasteningCarrier;
        }

        if (_placementWork.CarrierPresent
            && !_placementWork.CarrierSeated)
        {
            return MainConveyorState.SeatingPcbPlacementCarrier;
        }

        if (_inspectionWork.CarrierPresent)
        {
            if (_inspectionWork.Completed)
            {
                // A completed carrier either leaves now or waits off the belt.
                if (!_inspectionWork.CarrierSeated)
                {
                    if (!_inspectionWork.IsTransferAtWaitingPosition(live))
                        return MainConveyorState.WaitingForInspectionTransfer;
                    return !_repeat
                        && !_routeInspectionToNg()
                        && _inspectionWork.CanTransfer
                        && DownstreamReady
                        ? MainConveyorState.DischargingInspectionCarrier
                        : MainConveyorState.RaisingInspectionCarrier;
                }
            }
            else if (_inspectionWork.InspectionRequested
                || !(ExitCarrierDetected
                    ? DownstreamReady
                    : _placementWork.CanTransfer && _boltFasteningWork.CanReceive
                        || _placementWork.CanReceive
                            && (EntryCarrierDetected || !_repeat && UpstreamCarrierAvailable)))
            {
                // Once requested, keep the belt stopped through inspection and parking.
                if (_inspectionWork.InspectionRequested && _inspectionWork.AtInspectionPosition)
                    return MainConveyorState.WaitingForInspection;
                return _inspectionWork.PickupClear
                    ? MainConveyorState.PreparingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
            }
            else if (!_inspectionWork.CarrierSeated)
            {
                // Before inspection, lift S3 so the runnable logistics below can proceed.
                return _inspectionWork.IsTransferAtWaitingPosition(live)
                    ? MainConveyorState.RaisingInspectionCarrierForOtherTransfers
                    : MainConveyorState.WaitingForInspectionTransfer;
            }
        }

        if (ExitCarrierDetected)
        {
            return DownstreamReady
                ? MainConveyorState.DischargingInspectionCarrier
                : MainConveyorState.WaitingForRearEquipment;
        }

        if (!_repeat
            && !_routeInspectionToNg()
            && _inspectionWork.CanTransfer
            && _inspectionWork.IsTransferAtWaitingPosition(live)
            && DownstreamReady)
        {
            return MainConveyorState.DischargingInspectionCarrier;
        }

        if (_boltFasteningWork.CanTransfer && _inspectionWork.CanReceive)
        {
            return MainConveyorState.MovingBoltFasteningToInspection;
        }

        if (_placementWork.CanTransfer && _boltFasteningWork.CanReceive)
        {
            return MainConveyorState.MovingPcbPlacementToBoltFastening;
        }

        if (_placementWork.CanReceive
            && (EntryCarrierDetected || !_repeat && UpstreamCarrierAvailable))
        {
            return MainConveyorState.ReceivingFrontCarrier;
        }

        if (!_repeat
            && !_routeInspectionToNg()
            && _inspectionWork.CanTransfer
            && _inspectionWork.IsTransferAtWaitingPosition(live))
        {
            return MainConveyorState.WaitingForRearEquipment;
        }

        if (_boltFasteningWork.CarrierPresent)
        {
            return _boltFasteningWork.Completed
                ? MainConveyorState.WaitingForInspectionClear
                : MainConveyorState.WaitingForBoltFastening;
        }

        return _placementWork.CarrierPresent
            ? MainConveyorState.Idle
            : MainConveyorState.WaitingForFrontCarrier;
    }

    public Task PrepareEmptyStationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Preserve the support under an interrupted placement/fastening operation.
        // Station 3 also stays supported while the pickup still detects a carrier.
        var preparation = new List<Task>(3);
        if (_placementWork.CanReceive && _placement.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementBackupPlateUp, false, cancellationToken));
        if (_boltFasteningWork.CanReceive && _boltFastening.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningBackupPlateUp, false, cancellationToken));
        if (_inspectionWork.CanReceive && _inspection.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.InspectionBackupPlateUp, false, cancellationToken));
        return Task.WhenAll(preparation);
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await PrepareEmptyStationsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var state = State;
        TraceStep(state, waitingFor: state switch
        {
            MainConveyorState.WaitingForFrontCarrier =>
                "Entry carrier detected=ON OR Front 2 Available=ON (teaching: TEST, auto: DI)",
            MainConveyorState.WaitingForRearEquipment => "Rear Ready=ON (teaching: TEST, auto: DI)",
            MainConveyorState.WaitingForInspection =>
                "S3 inspection complete and transfer returned to NG pickup; conveyor remains stopped",
            MainConveyorState.WaitingForInspectionTransfer =>
                "NG pickup raised, empty and at its waiting position",
            MainConveyorState.WaitingForBoltFastening =>
                $"S2 work complete; enabled={_boltFasteningWork.Enabled}, completed={_boltFasteningWork.Completed}, "
                    + $"plate={_boltFasteningWork.BackupPlate}, stopper={_boltFasteningWork.Stopper}, "
                    + $"canTransfer={_boltFasteningWork.CanTransfer}, work={_boltFasteningWork.CurrentJob.Id}",
            MainConveyorState.WaitingForInspectionClear =>
                $"S3 vacant and NG pickup empty; S2 enabled={_boltFasteningWork.Enabled}, "
                    + $"completed={_boltFasteningWork.Completed}, canTransfer={_boltFasteningWork.CanTransfer}; "
                    + $"S3 canReceive={_inspectionWork.CanReceive}, HS1={_inspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink1)}, "
                    + $"HS2={_inspectionWork.HeatSinkPresent(HeatSinkSlot.HeatSink2)}, "
                    + $"NG carrier detected={_io.GetInput(InputIo.NgCarrierDetected)}",
            MainConveyorState.Idle => "station work complete and destination vacant",
            _ => null,
        });
        var transferring = state is MainConveyorState.ReceivingFrontCarrier
            or MainConveyorState.MovingPcbPlacementToBoltFastening
            or MainConveyorState.MovingBoltFasteningToInspection
            or MainConveyorState.DischargingInspectionCarrier;
        try
        {
            if (transferring)
            {
                _executingTransfer = state;
                Changed?.Invoke();
            }
            switch (state)
            {
                case MainConveyorState.PreparingInspectionCarrier:
                    var inspectionJob = _inspectionWork.CurrentJob;
                    await _io.SetOutputAndWaitAsync(OutputIo.InspectionStopperUp, true, cancellationToken);
                    await _io.SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, false, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_inspectionWork.InspectionRequested
                        && (ExitCarrierDetected
                            ? DownstreamReady
                            : _placementWork.CanTransfer && _boltFasteningWork.CanReceive
                                || _placementWork.CanReceive
                                    && (EntryCarrierDetected || !_repeat && UpstreamCarrierAvailable)))
                        break;
                    _inspectionWork.RequestInspection(inspectionJob);
                    break;
                case MainConveyorState.RaisingInspectionCarrierForOtherTransfers:
                case MainConveyorState.RaisingInspectionCarrier:
                    await _inspection.SeatAsync(cancellationToken);
                    break;
                case MainConveyorState.SeatingBoltFasteningCarrier:
                case MainConveyorState.SeatingPcbPlacementCarrier:
                    // S1/S2 can prepare their work without moving the belt.
                    var seating = new List<Task>(2);
                    if (_boltFasteningWork.CarrierPresent && !_boltFasteningWork.CarrierSeated)
                        seating.Add(_boltFastening.SeatAsync(cancellationToken));
                    if (_placementWork.CarrierPresent && !_placementWork.CarrierSeated)
                        seating.Add(_placement.SeatAsync(cancellationToken));
                    await Task.WhenAll(seating);
                    cancellationToken.ThrowIfCancellationRequested();
                    break;
                case MainConveyorState.DischargingInspectionCarrier:
                    await DischargeInspectionAsync(cancellationToken);
                    break;
                case MainConveyorState.MovingBoltFasteningToInspection:
                    await MoveCarrierAsync(_boltFasteningWork, _inspectionWork, cancellationToken);
                    break;
                case MainConveyorState.MovingPcbPlacementToBoltFastening:
                    await MoveCarrierAsync(_placementWork, _boltFasteningWork, cancellationToken);
                    break;
                case MainConveyorState.ReceivingFrontCarrier:
                    await ReceiveAtPlacementAsync(cancellationToken);
                    break;
                default:
                    UpdateSmema();
                    await WaitForChangeAsync(cancellationToken);
                    break;
            }
        }
        finally
        {
            if (transferring)
            {
                _executingTransfer = MainConveyorState.Idle;
                Changed?.Invoke();
            }
        }
    }

    private void UpdateSmema()
    {
        var rearAvailable = ExitCarrierDetected
            || !_repeat
                && !_routeInspectionToNg()
                && _inspectionWork.CanTransfer
                && _inspectionWork.IsTransferAtWaitingPosition();
        _io.SetAutomaticSmemaOutput(
            OutputIo.MainConveyorReadyToFront2,
            !_repeat && _placementWork.CanReceive && !rearAvailable);
        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, rearAvailable);
    }
}
