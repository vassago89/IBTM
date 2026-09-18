using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor
{
    public MainConveyorState GetState(bool runCommandOn, bool live = true)
    {
        // 실행 중인 이송은 출발지 하강부터 목적지 도착까지 한 동작으로 유지한다.
        var executingTransfer = _executingTransfer;
        if (executingTransfer != MainConveyorState.Idle)
            return executingTransfer;
        if (runCommandOn)
            return MainConveyorState.Running;

        // S1/S2 착좌는 벨트 이송보다 먼저 처리한다.
        if (_boltFasteningWork.Station.CarrierPresent
            && !_boltFasteningWork.Station.CarrierSeated)
        {
            return MainConveyorState.SeatingBoltFasteningCarrier;
        }

        if (_placementWork.Station.CarrierPresent
            && !_placementWork.Station.CarrierSeated)
        {
            return MainConveyorState.SeatingPcbPlacementCarrier;
        }

        if (_inspectionWork.Station.CarrierPresent)
        {
            if (_inspectionWork.Completed)
            {
                // 검사 완료: 바로 배출할 수 없으면 플레이트를 올려 벨트에서 분리한다.
                if (!_inspectionWork.Station.CarrierSeated)
                {
                    if (!_inspectionWork.IsTransferAtWaitingPosition(live))
                        return MainConveyorState.WaitingForInspectionTransfer;
                    if (!_repeat
                        && !_routeInspectionToNg()
                        && _inspectionWork.CanTransfer
                        && DownstreamReady)
                    {
                        return MainConveyorState.DischargingInspectionCarrier;
                    }

                    return MainConveyorState.RaisingInspectionCarrier;
                }
            }
            else if (_inspectionWork.InspectionRequested
                || !(ExitCarrierDetected
                    ? DownstreamReady
                    : (_placementWork.CanTransfer && _boltFasteningWork.CanReceive)
                        || (_placementWork.CanReceive
                            && (EntryCarrierDetected || !_repeat && UpstreamCarrierAvailable))))
            {
                // 검사 요청 이후에는 검사와 NG 픽업 위치 복귀가 끝날 때까지 벨트를 정지한다.
                if (_inspectionWork.InspectionRequested && _inspectionWork.AtInspectionPosition)
                    return MainConveyorState.WaitingForInspection;
                return _inspectionWork.PickupClear
                    ? MainConveyorState.PreparingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
            }
            else if (!_inspectionWork.Station.CarrierSeated)
            {
                // 검사 전에는 S3를 올려 아래의 반입·이송을 먼저 처리한다.
                return _inspectionWork.IsTransferAtWaitingPosition(live)
                    ? MainConveyorState.RaisingInspectionCarrierForOtherTransfers
                    : MainConveyorState.WaitingForInspectionTransfer;
            }
        }

        // 이송 우선순위: 출구 잔류 → S3 배출 → S2→S3 → S1→S2 → 전단 반입.
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

        if (_boltFasteningWork.Station.CarrierPresent)
        {
            return _boltFasteningWork.Completed
                ? MainConveyorState.WaitingForInspectionClear
                : MainConveyorState.WaitingForBoltFastening;
        }

        return _placementWork.Station.CarrierPresent
            ? MainConveyorState.Idle
            : MainConveyorState.WaitingForFrontCarrier;
    }

    public Task PrepareEmptyStationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Preserve the support under an interrupted placement/fastening operation.
        // Station 3 also stays supported while the pickup still detects a carrier.
        var preparation = new List<Task>(3);
        if (_placementWork.CanReceive && _placementWork.Station.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementBackupPlateUp, false, cancellationToken));
        if (_boltFasteningWork.CanReceive && _boltFasteningWork.Station.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningBackupPlateUp, false, cancellationToken));
        if (_inspectionWork.CanReceive && _inspectionWork.Station.BackupPlate != StationCylinderState.Down)
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
                    + $"plate={_boltFasteningWork.Station.BackupPlate}, stopper={_boltFasteningWork.Station.Stopper}, "
                    + $"canTransfer={_boltFasteningWork.CanTransfer}, work={_boltFasteningWork.CurrentJob.Id}",
            MainConveyorState.WaitingForInspectionClear =>
                $"S3 vacant and NG pickup empty; S2 enabled={_boltFasteningWork.Enabled}, "
                    + $"completed={_boltFasteningWork.Completed}, canTransfer={_boltFasteningWork.CanTransfer}; "
                    + $"S3 canReceive={_inspectionWork.CanReceive}, HS1={_inspectionWork.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1)}, "
                    + $"HS2={_inspectionWork.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2)}, "
                    + $"NG carrier detected={_io.GetInput(InputIo.NgCarrierDetected)}",
            MainConveyorState.Idle => "station work complete and destination vacant",
            _ => null,
        });
        try
        {
            if (state is MainConveyorState.ReceivingFrontCarrier
                or MainConveyorState.MovingPcbPlacementToBoltFastening
                or MainConveyorState.MovingBoltFasteningToInspection
                or MainConveyorState.DischargingInspectionCarrier)
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
                    await _inspectionWork.Station.SeatAsync(cancellationToken);
                    break;
                case MainConveyorState.SeatingBoltFasteningCarrier:
                case MainConveyorState.SeatingPcbPlacementCarrier:
                    // S1/S2 can prepare their work without moving the belt.
                    var seating = new List<Task>(2);
                    if (_boltFasteningWork.Station.CarrierPresent && !_boltFasteningWork.Station.CarrierSeated)
                        seating.Add(_boltFasteningWork.Station.SeatAsync(cancellationToken));
                    if (_placementWork.Station.CarrierPresent && !_placementWork.Station.CarrierSeated)
                        seating.Add(_placementWork.Station.SeatAsync(cancellationToken));
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
            if (_executingTransfer != MainConveyorState.Idle)
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
