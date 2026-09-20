using System;
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
        switch (true)
        {
            case true when executingTransfer != MainConveyorState.Idle:
                return executingTransfer;
            case true when runCommandOn:
                return MainConveyorState.Running;
            // S1/S2 착좌는 벨트 이송보다 먼저 처리한다.
            case true when _boltFasteningWork.Station.CarrierPresent
                && !_boltFasteningWork.Station.CarrierSeated:
                return MainConveyorState.SeatingCarriers;
            case true when _placementWork.Station.CarrierPresent
                && !_placementWork.Station.CarrierSeated:
                return MainConveyorState.SeatingCarriers;
        }

        var transfer = GetNextTransfer(live, live ? null : runCommandOn);
        switch (true)
        {
            case true when !_inspectionWork.Station.CarrierPresent:
                return transfer;
            case true when _inspectionWork.Completed:
                // 검사 완료: 바로 배출할 수 없으면 플레이트를 올려 벨트에서 분리한다.
                switch (true)
                {
                    case true when _inspectionWork.Station.CarrierSeated:
                        return transfer;
                    case true when !_inspectionWork.IsTransferAtWaitingPosition(live):
                        return MainConveyorState.WaitingForInspectionTransfer;
                    case true when !_repeat
                        && !IsNgTransferRequired
                        && _inspectionWork.IsTransferAllowedFor(live ? null : runCommandOn)
                        && DownstreamReady:
                        return MainConveyorState.DischargingInspectionCarrier;
                    default:
                        return MainConveyorState.RaisingInspectionCarrier;
                }
            // 검사 전에는 S3를 올려 다른 물류를 먼저 처리한다.
            case true when !_inspectionWork.InspectionRequested
                && transfer is MainConveyorState.DischargingInspectionCarrier
                    or MainConveyorState.MovingPcbPlacementToBoltFastening
                    or MainConveyorState.ReceivingFrontCarrier:
                if (_inspectionWork.Station.CarrierSeated)
                    return transfer;
                return _inspectionWork.IsTransferAtWaitingPosition(live)
                    ? MainConveyorState.RaisingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
            // 검사 요청 이후에는 검사와 NG 픽업 위치 복귀가 끝날 때까지 벨트를 정지한다.
            case true when _inspectionWork.InspectionRequested && _inspectionWork.IsAtInspectionPosition(live ? null : runCommandOn):
                return MainConveyorState.WaitingForInspection;
            default:
                return _inspectionWork.PickupClear
                    ? MainConveyorState.PreparingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
        }
    }

    private MainConveyorState GetNextTransfer(bool live = true, bool? conveyorRunning = null)
    {
        // 이송 우선순위: 출구 잔류 → S3 배출 → S2→S3 → S1→S2 → 전단 반입.
        switch (true)
        {
            case true when ExitCarrierDetected:
                return DownstreamReady
                    ? MainConveyorState.DischargingInspectionCarrier
                    : MainConveyorState.WaitingForRearEquipment;
            case true when !_repeat
                && !IsNgTransferRequired
                && _inspectionWork.IsTransferAllowedFor(conveyorRunning)
                && _inspectionWork.IsTransferAtWaitingPosition(live)
                && DownstreamReady:
                return MainConveyorState.DischargingInspectionCarrier;
            case true when (!_repeat || ReferenceEquals(RepeatEndWork, _inspectionWork))
                && _boltFasteningWork.IsTransferAllowed && _inspectionWork.IsReceiveAllowed:
                return MainConveyorState.MovingBoltFasteningToInspection;
            case true when (!_repeat || !ReferenceEquals(RepeatEndWork, _placementWork))
                && _placementWork.IsTransferAllowed && _boltFasteningWork.IsReceiveAllowed:
                return MainConveyorState.MovingPcbPlacementToBoltFastening;
            case true when _placementWork.IsReceiveAllowed
                && (EntryCarrierDetected || !_repeat && UpstreamCarrierAvailable):
                return MainConveyorState.ReceivingFrontCarrier;
            case true when !_repeat
                && !IsNgTransferRequired
                && _inspectionWork.IsTransferAllowedFor(conveyorRunning)
                && _inspectionWork.IsTransferAtWaitingPosition(live):
                return MainConveyorState.WaitingForRearEquipment;
            case true when _boltFasteningWork.Station.CarrierPresent:
                return _boltFasteningWork.Completed
                    ? MainConveyorState.WaitingForInspectionClear
                    : MainConveyorState.WaitingForBoltFastening;
            default:
                return _placementWork.Station.CarrierPresent
                    ? MainConveyorState.WaitingForPcbPlacement
                    : MainConveyorState.WaitingForFrontCarrier;
        }
    }

    public Task PrepareEmptyStationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Preserve the support under an interrupted placement/fastening operation.
        // Keep Station 3 supported while an NG transfer has not released its grip.
        var preparation = new List<Task>(3);
        if (_placementWork.IsReceiveAllowed && _placementWork.Station.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementBackupPlateUp, false, cancellationToken));
        if (_boltFasteningWork.IsReceiveAllowed && _boltFasteningWork.Station.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningBackupPlateUp, false, cancellationToken));
        if (_inspectionWork.IsReceiveAllowed && _inspectionWork.Station.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.InspectionBackupPlateUp, false, cancellationToken));
        return Task.WhenAll(preparation);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        Stop();
        using var runCancellation = _operations.Link(cancellationToken);
        _runCancellation = runCancellation;
        runCancellation.Disposed += () =>
        {
            if (ReferenceEquals(_runCancellation, runCancellation))
                _runCancellation = null;
        };
        cancellationToken = runCancellation.Token;
        using var motor = new ConveyorRun(
            _io, OutputIo.MainConveyorRun, cancellationToken,
            OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
        _repeat = repeat;
        try
        {
            BeginRun();
            while (!cancellationToken.IsCancellationRequested)
            {
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
                    MainConveyorState.WaitingForPcbPlacement =>
                        $"S1 placement complete; enabled={_placementWork.Enabled}, completed={_placementWork.Completed}, "
                            + $"work={_placementWork.CurrentJob.Id}",
                    MainConveyorState.WaitingForBoltFastening =>
                        $"S2 work complete; enabled={_boltFasteningWork.Enabled}, completed={_boltFasteningWork.Completed}, "
                            + $"plate={_boltFasteningWork.Station.BackupPlate}, stopper={_boltFasteningWork.Station.Stopper}, "
                            + $"canTransfer={_boltFasteningWork.IsTransferAllowed}, work={_boltFasteningWork.CurrentJob.Id}",
                    MainConveyorState.WaitingForInspectionClear =>
                        $"S3 vacant and NG pickup empty; S2 enabled={_boltFasteningWork.Enabled}, "
                            + $"completed={_boltFasteningWork.Completed}, canTransfer={_boltFasteningWork.IsTransferAllowed}; "
                            + $"S3 canReceive={_inspectionWork.IsReceiveAllowed}, HS1={_inspectionWork.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1)}, "
                            + $"HS2={_inspectionWork.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2)}, "
                            + $"NG carrier detected={_io.GetInput(InputIo.NgCarrierDetected)}",
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
                                && GetNextTransfer() is MainConveyorState.DischargingInspectionCarrier
                                    or MainConveyorState.MovingPcbPlacementToBoltFastening
                                    or MainConveyorState.ReceivingFrontCarrier)
                                break;
                            _inspectionWork.RequestInspection(inspectionJob);
                            break;
                        case MainConveyorState.RaisingInspectionCarrier:
                            await _inspectionWork.Station.SeatAsync(cancellationToken);
                            break;
                        case MainConveyorState.SeatingCarriers:
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
                            await TransferAsync(_boltFasteningWork, _inspectionWork, cancellationToken);
                            break;
                        case MainConveyorState.MovingPcbPlacementToBoltFastening:
                            await TransferAsync(_placementWork, _boltFasteningWork, cancellationToken);
                            break;
                        case MainConveyorState.ReceivingFrontCarrier:
                            await TransferAsync(null, _placementWork, cancellationToken);
                            break;
                        default:
                            var rearAvailable = ExitCarrierDetected
                                || !_repeat
                                    && !IsNgTransferRequired
                                    && _inspectionWork.IsTransferAllowed
                                    && _inspectionWork.IsTransferAtWaitingPosition();
                            _io.SetAutomaticSmemaOutput(
                                OutputIo.MainConveyorReadyToFront2,
                                !_repeat && _placementWork.IsReceiveAllowed && !rearAvailable
                                    && (!_inspectionWork.Station.CarrierPresent || _inspectionWork.Station.CarrierSeated));
                            _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, rearAvailable);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
        finally
        {
            _repeat = false;
            _inspectionWork.ClearInspectionRequest();
            EndRun(cancellationToken);
        }
    }
}
