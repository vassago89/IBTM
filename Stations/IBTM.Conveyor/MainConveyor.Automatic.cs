using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor
{
    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        using var runCancellation = BeginConveyorOperation(cancellationToken);
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
                var step = GetNextStep(RunCommandOn);
                if (!await ExecuteStepAsync(step, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
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
            _inspection.ClearInspectionRequest();
            EndRun(cancellationToken);
        }
    }

    public MainConveyorState GetNextStep(bool runCommandOn, bool live = true)
    {
        switch (true)
        {
            case true when runCommandOn:
                return MainConveyorState.Running;
            case true when _inspection.CarrierSeatingRequested:
                return MainConveyorState.WaitingForInspectionTransfer;
            // S1/S2 착좌는 벨트 이송보다 먼저 처리한다.
            case true when _fastening.CarrierPresent
                && !_fastening.CarrierSeated:
                return MainConveyorState.SeatingCarriers;
            case true when _placement.CarrierPresent
                && !_placement.CarrierSeated:
                return MainConveyorState.SeatingCarriers;
        }

        var transfer = GetNextTransfer(live, live ? null : runCommandOn);
        switch (true)
        {
            case true when !_inspection.Station.CarrierPresent:
                return transfer;
            case true when _inspection.Station.Completed:
                // 검사 완료: 바로 배출할 수 없으면 플레이트를 올려 벨트에서 분리한다.
                switch (true)
                {
                    case true when _inspection.Station.CarrierSeated:
                        return transfer;
                    case true when !_repeat
                        && !IsNgTransferRequired
                        && _inspection.IsTransferAllowedFor(live ? null : runCommandOn)
                        && DownstreamReady:
                        return _inspection.IsTransferAtWaitingPosition(live)
                            ? MainConveyorState.DischargingInspectionCarrier
                            : MainConveyorState.WaitingForInspectionTransfer;
                    default:
                        return _inspection.PickupClear
                            && _units.IsMotionEnabled(MotionGroup.InspectionGantry)
                            ? MainConveyorState.RaisingInspectionCarrier
                            : MainConveyorState.WaitingForInspectionTransfer;
                }
            // 검사 전에는 S3를 올려 다른 물류를 먼저 처리한다.
            case true when !_inspection.InspectionRequested
                && transfer is MainConveyorState.DischargingInspectionCarrier
                    or MainConveyorState.MovingPcbPlacementToBoltFastening
                    or MainConveyorState.ReceivingFrontCarrier:
                if (_inspection.Station.CarrierSeated)
                    return transfer;
                return _inspection.PickupClear
                    && _units.IsMotionEnabled(MotionGroup.InspectionGantry)
                    ? MainConveyorState.RaisingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
            // 검사 요청 이후에는 검사와 전용 대기 위치 복귀가 끝날 때까지 벨트를 정지한다.
            case true when _inspection.InspectionRequested && _inspection.IsAtInspectionPosition(live ? null : runCommandOn):
                return MainConveyorState.WaitingForInspection;
            default:
                return _inspection.PickupClear
                    ? MainConveyorState.PreparingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
        }
    }

    private async Task<bool> ExecuteStepAsync(MainConveyorState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterStep(state, waitingFor: state switch
        {
            MainConveyorState.WaitingForFrontCarrier =>
                "Entry carrier detected=ON OR Front 2 Available=ON (teaching: TEST, auto: DI)",
            MainConveyorState.WaitingForRearEquipment => "Rear Ready=ON (teaching: TEST, auto: DI)",
            MainConveyorState.WaitingForInspection =>
                "S3 inspection complete; conveyor remains stopped",
            MainConveyorState.WaitingForInspectionTransfer =>
                "inspection gantry operation complete; carrier seating moves to NG pickup before raising S3",
            MainConveyorState.WaitingForPcbPlacement =>
                $"S1 placement complete; enabled={_units.PcbPlacement}, completed={_placement.Completed}, "
                    + $"work={_placement.CurrentJob.Id}",
            MainConveyorState.WaitingForBoltFastening =>
                $"S2 work complete; enabled={_units.BoltFastening}, completed={_fastening.Completed}, "
                    + $"plate={_fastening.BackupPlate}, stopper={_fastening.Stopper}, "
                    + $"canTransfer={_fastening.IsTransferAllowed}, work={_fastening.CurrentJob.Id}",
            MainConveyorState.WaitingForInspectionClear =>
                $"S3 vacant and NG pickup empty; S2 enabled={_units.BoltFastening}, "
                    + $"completed={_fastening.Completed}, canTransfer={_fastening.IsTransferAllowed}; "
                    + $"S3 canReceive={_inspection.IsReceiveAllowed}, HS1={_inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1)}, "
                    + $"HS2={_inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2)}",
            _ => null,
        });
        switch (state)
        {
            case MainConveyorState.PreparingInspectionCarrier:
                var inspectionJob = _inspection.Station.CurrentJob;
                await _io.SetOutputAndWaitAsync(OutputIo.InspectionStopperUp, true, cancellationToken);
                await _io.SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!_inspection.InspectionRequested
                    && GetNextTransfer() is MainConveyorState.DischargingInspectionCarrier
                        or MainConveyorState.MovingPcbPlacementToBoltFastening
                        or MainConveyorState.ReceivingFrontCarrier)
                    break;
                _inspection.RequestInspection(inspectionJob);
                break;
            case MainConveyorState.RaisingInspectionCarrier:
                _inspection.RequestCarrierSeating(_inspection.Station.CurrentJob);
                return false;
            case MainConveyorState.SeatingCarriers:
                // S1/S2 can prepare their work without moving the belt.
                var seating = new List<Task>(2);
                if (_fastening.CarrierPresent && !_fastening.CarrierSeated)
                    seating.Add(_fastening.SeatAsync(cancellationToken));
                if (_placement.CarrierPresent && !_placement.CarrierSeated)
                    seating.Add(_placement.SeatAsync(cancellationToken));
                await Task.WhenAll(seating);
                cancellationToken.ThrowIfCancellationRequested();
                break;
            case MainConveyorState.DischargingInspectionCarrier:
                await DischargeInspectionAsync(cancellationToken);
                break;
            case MainConveyorState.MovingBoltFasteningToInspection:
                await TransferAsync(_fastening, _inspection.Station, cancellationToken);
                break;
            case MainConveyorState.MovingPcbPlacementToBoltFastening:
                await TransferAsync(_placement, _fastening, cancellationToken);
                break;
            case MainConveyorState.ReceivingFrontCarrier:
                await TransferAsync(null, _placement, cancellationToken);
                break;
            default:
                var rearAvailable = !_repeat
                    && !IsNgTransferRequired
                    && _inspection.IsTransferAllowed
                    && _inspection.IsTransferAtWaitingPosition();
                SetSmemaOutput(
                    OutputIo.MainConveyorReadyToFront2,
                    !_repeat && _placement.IsReceiveAllowed && !rearAvailable
                        && (!_inspection.Station.CarrierPresent || _inspection.Station.CarrierSeated));
                SetSmemaOutput(OutputIo.MainConveyorAvailableToRear, rearAvailable);
                return false;
        }
        return true;
    }

    private MainConveyorState GetNextTransfer(bool live = true, bool? conveyorRunning = null)
    {
        // 이송 우선순위: S3 배출 → S2→S3 → S1→S2 → 전단 반입.
        switch (true)
        {
            case true when !_repeat
                && !IsNgTransferRequired
                && _inspection.IsTransferAllowedFor(conveyorRunning)
                && _inspection.IsTransferAtWaitingPosition(live)
                && DownstreamReady:
                return MainConveyorState.DischargingInspectionCarrier;
            case true when (!_repeat || ReferenceEquals(RepeatEndStation, _inspection.Station))
                && _fastening.IsTransferAllowed && _inspection.IsReceiveAllowed:
                return MainConveyorState.MovingBoltFasteningToInspection;
            case true when (!_repeat || !ReferenceEquals(RepeatEndStation, _placement))
                && _placement.IsTransferAllowed && _fastening.IsReceiveAllowed:
                return MainConveyorState.MovingPcbPlacementToBoltFastening;
            case true when _placement.IsReceiveAllowed
                && (EntryCarrierDetected || !_repeat && UpstreamCarrierAvailable):
                return MainConveyorState.ReceivingFrontCarrier;
            case true when !_repeat
                && !IsNgTransferRequired
                && _inspection.IsTransferAllowedFor(conveyorRunning)
                && _inspection.IsTransferAtWaitingPosition(live):
                return MainConveyorState.WaitingForRearEquipment;
            case true when _fastening.CarrierPresent:
                return _fastening.Completed
                    ? MainConveyorState.WaitingForInspectionClear
                    : MainConveyorState.WaitingForBoltFastening;
            default:
                return _placement.CarrierPresent
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
        if (_placement.IsReceiveAllowed && _placement.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementBackupPlateUp, false, cancellationToken));
        if (_fastening.IsReceiveAllowed && _fastening.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningBackupPlateUp, false, cancellationToken));
        if (_inspection.IsReceiveAllowed && _inspection.Station.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.InspectionBackupPlateUp, false, cancellationToken));
        return Task.WhenAll(preparation);
    }



}
