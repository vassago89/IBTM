using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor : AutoUnit
{
    private readonly ConveyorSettings _settings;
    private readonly OperationCancellation _operations;
    private readonly ConveyorStation _placement;
    private readonly ConveyorStation _fastening;
    private readonly InspectionStation _inspection;
    private readonly UnitSettings _units;
    private OperationCancellation.Operation? _runCancellation;
    private bool _repeat;
    // Commissioning inputs, kept only for this application session.
    private volatile bool _testUpstreamCarrierAvailable;
    private volatile bool _testDownstreamReady;

    public MainConveyor(
        IIoService io,
        ConveyorSettings settings,
        OperationCancellation operations,
        ConveyorStation placement,
        ConveyorStation fastening,
        InspectionStation inspection,
        UnitSettings units)
        : base(io, [
            InputIo.AutoMode,
            InputIo.MainConveyorAvailableFromFront2,
            InputIo.MainConveyorReadyFromRear,
            InputIo.MainConveyorEntryCarrierDetected,
        ])
    {
        _settings = settings;
        _operations = operations;
        _placement = placement;
        _fastening = fastening;
        _inspection = inspection;
        _units = units;
        placement.Changed += NotifyChanged;
        fastening.Changed += NotifyChanged;
        inspection.Changed += NotifyChanged;
    }

    public bool IsTransferPaused
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            WakeRun();
        }
    }

    private bool IsNgTransferRequired => _units.Inspection && _inspection.RouteToNg;

    private bool IsRearDischargeAllowed
    {
        get
        {
            return !_repeat
                && !IsNgTransferRequired
                && _inspection.IsTransferAllowed
                && _inspection.IsRearDischargeReady;
        }
    }

    public bool UpstreamCarrierAvailable
    {
        get
        {
            return Io.GetInput(InputIo.AutoMode)
                ? _testUpstreamCarrierAvailable
                : Io.GetInput(InputIo.MainConveyorAvailableFromFront2);
        }
    }

    public bool DownstreamReady
    {
        get
        {
            return Io.GetInput(InputIo.AutoMode)
                ? _testDownstreamReady
                : Io.GetInput(InputIo.MainConveyorReadyFromRear);
        }
    }

    public bool TestUpstreamCarrierAvailable
    {
        get => _testUpstreamCarrierAvailable;
        set
        {
            // The selector contact is ON in teaching/manual mode.
            value = value && Io.IsReady && Io.GetInput(InputIo.AutoMode);
            if (_testUpstreamCarrierAvailable == value)
                return;
            _testUpstreamCarrierAvailable = value;
            NotifyChanged();
        }
    }

    public bool TestDownstreamReady
    {
        get => _testDownstreamReady;
        set
        {
            value = value && Io.IsReady && Io.GetInput(InputIo.AutoMode);
            if (_testDownstreamReady == value)
                return;
            _testDownstreamReady = value;
            NotifyChanged();
        }
    }

    protected override void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.AutoMode && !value)
        {
            _testUpstreamCarrierAvailable = false;
            _testDownstreamReady = false;
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        using var runCancellation = BeginConveyorOperation(cancellationToken);
        cancellationToken = runCancellation.Token;
        var motor = new ConveyorRun(
            Io, OutputIo.MainConveyorRun, cancellationToken,
            OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
        _repeat = repeat;
        try
        {
            BeginRun();
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = GetNextStep(Io.GetOutput(OutputIo.MainConveyorRun));
                cancellationToken.ThrowIfCancellationRequested();
                SetSmemaOutput(OutputIo.MainConveyorAvailableToRear, !IsTransferPaused && IsRearDischargeAllowed, cancellationToken);
                switch (state)
                {
                    case MainConveyorState.PreparingInspectionCarrier:
                        EnterStep(state);
                        var inspectionJob = _inspection.Station.CurrentJob;
                        await Io.SetOutputAndWaitAsync(OutputIo.InspectionStopperUp, true, cancellationToken);
                        await Io.SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, false, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!_inspection.InspectionRequested
                            && NextTransfer is MainConveyorState.DischargingInspectionCarrier
                                or MainConveyorState.MovingPcbPlacementToBoltFastening
                                or MainConveyorState.ReceivingFrontCarrier)
                            continue;
                        _inspection.RequestInspection(inspectionJob);
                        continue;
                    case MainConveyorState.RaisingInspectionCarrier:
                        EnterStep(state);
                        _inspection.RequestCarrierSeating(_inspection.Station.CurrentJob);
                        await WaitForChangeAsync(cancellationToken);
                        continue;
                    case MainConveyorState.SeatingCarriers:
                        EnterStep(state);
                        // S1/S2 can prepare their work without moving the belt.
                        var seating = new List<Task>(2);
                        if (_fastening.CarrierPresent && !_fastening.CarrierSeated)
                            seating.Add(_fastening.SeatAsync(cancellationToken));
                        if (_placement.CarrierPresent && !_placement.CarrierSeated)
                            seating.Add(_placement.SeatAsync(cancellationToken));
                        await Task.WhenAll(seating);
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    case MainConveyorState.DischargingInspectionCarrier:
                    {
                        EnterStep(state);
                        if (!IsRearDischargeAllowed)
                        {
                            throw new MotionInterlockException(
                                "Rear discharge requires a completed carrier and the raised, clear inspection pickup at its waiting position.");
                        }
                        var departingJob = _inspection.Station.CurrentJob;
                        var rearReleased = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var extraRun = TimeSpan.FromSeconds(_settings.RearSmemaOffDelaySeconds);
                        var timeout = TimeSpan.FromSeconds(_settings.TransferTimeoutSeconds);
                        void ObserveRear()
                        {
                            if (!DownstreamReady)
                                rearReleased.TrySetResult(Stopwatch.GetTimestamp());
                        }
                        Changed += ObserveRear;
                        using var discharge = new ConveyorRun(
                            Io, OutputIo.MainConveyorRun, cancellationToken,
                            OutputIo.MainConveyorAvailableToRear);
                        try
                        {
                            SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false, cancellationToken);
                            ObserveRear();
                            if (rearReleased.Task.IsCompleted)
                                continue;
                            _inspection.Station.RequireCurrentJob(departingJob);
                            await _inspection.Station.ReleaseAsync(cancellationToken);

                            if (rearReleased.Task.IsCompleted)
                                continue;
                            _inspection.Station.RequireCurrentJob(departingJob);
                            if (_inspection.Station.BackupPlate != StationCylinderState.Down
                                || _inspection.Station.Stopper != StationCylinderState.Down
                                || !_inspection.IsRearDischargeReady)
                            {
                                throw new MotionInterlockException(
                                    "Rear discharge lost support release or inspection pickup clearance before starting the belt.");
                            }
                            EnterStep(MainConveyorState.DischargingInspectionCarrier, waitingFor: "Rear Ready=OFF");
                            // Finish motor setup before the final READY check.
                            cancellationToken.ThrowIfCancellationRequested();
                            Io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
                            cancellationToken.ThrowIfCancellationRequested();
                            Io.SetOutput(OutputIo.MainConveyorForward, true);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (rearReleased.Task.IsCompleted || !DownstreamReady)
                                continue;
                            cancellationToken.ThrowIfCancellationRequested();
                            Io.SetOutput(OutputIo.MainConveyorRun, true);
                            try
                            {
                                await rearReleased.Task.WaitAsync(timeout, cancellationToken);
                            }
                            catch (TimeoutException)
                            {
                                throw new IoTimeoutException(
                                    InputIo.MainConveyorReadyFromRear, false, (int)timeout.TotalMilliseconds);
                            }

                            EnterStep(MainConveyorState.DischargingInspectionCarrier,
                                target: $"Rear Ready OFF; extra run {extraRun.TotalSeconds} s");
                            // Only this discharge owns the OFF timestamp; STOP discards the remaining delay.
                            var remaining = extraRun - Stopwatch.GetElapsedTime(await rearReleased.Task);
                            if (remaining > TimeSpan.Zero)
                                await Task.Delay(remaining, cancellationToken);
                        }
                        catch (Exception exception)
                        {
                            discharge.Failure = exception;
                        }
                        finally
                        {
                            Changed -= ObserveRear;
                        }
                        continue;
                    }
                    case MainConveyorState.MovingBoltFasteningToInspection:
                        EnterStep(state);
                        await TransferAsync(_fastening, _inspection.Station, cancellationToken);
                        continue;
                    case MainConveyorState.MovingPcbPlacementToBoltFastening:
                        EnterStep(state);
                        await TransferAsync(_placement, _fastening, cancellationToken);
                        continue;
                    case MainConveyorState.ReceivingFrontCarrier:
                        EnterStep(state);
                        await TransferAsync(null, _placement, cancellationToken);
                        continue;
                    case MainConveyorState.WaitingForFrontCarrier:
                        EnterStep(state, waitingFor:
                            "Entry carrier detected=ON OR Front 2 Available=ON (teaching: TEST, auto: DI)");
                        break;
                    case MainConveyorState.WaitingForRearEquipment:
                        EnterStep(state, waitingFor: "Rear Ready=ON (teaching: TEST, auto: DI)");
                        break;
                    case MainConveyorState.WaitingForInspection:
                        EnterStep(state, waitingFor: "S3 inspection complete; conveyor remains stopped");
                        break;
                    case MainConveyorState.WaitingForInspectionTransfer:
                        EnterStep(state, waitingFor:
                            "inspection gantry operation complete; carrier seating moves to NG pickup before raising S3");
                        break;
                    case MainConveyorState.WaitingForPcbPlacement:
                        EnterStep(state, waitingFor:
                            $"S1 placement complete; enabled={_units.PcbPlacement}, completed={_placement.Completed}, "
                                + $"work={_placement.CurrentJob.Id}");
                        break;
                    case MainConveyorState.WaitingForBoltFastening:
                        EnterStep(state, waitingFor:
                            $"S2 work complete; enabled={_units.BoltFastening}, completed={_fastening.Completed}, "
                                + $"plate={_fastening.BackupPlate}, stopper={_fastening.Stopper}, "
                                + $"work={_fastening.CurrentJob.Id}");
                        break;
                    case MainConveyorState.WaitingForInspectionClear:
                        EnterStep(state, waitingFor:
                            $"S3 vacant and NG pickup empty; S2 enabled={_units.BoltFastening}, "
                                + $"completed={_fastening.Completed}; "
                                + $"S3 canReceive={_inspection.IsReceiveAllowed}, HS1={_inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1)}, "
                                + $"HS2={_inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2)}");
                        break;
                    case MainConveyorState.Running:
                        EnterStep(state);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported main conveyor step.");
                }

                if (!IsTransferPaused
                    && state is MainConveyorState.WaitingForFrontCarrier
                        or MainConveyorState.WaitingForRearEquipment
                        or MainConveyorState.WaitingForBoltFastening
                        or MainConveyorState.WaitingForInspectionClear
                    && !_repeat && !_placement.CarrierPresent
                    && (!_inspection.Station.CarrierPresent || _inspection.Station.CarrierSeated))
                {
                    await TransferAsync(null, _placement, cancellationToken);
                }
                else
                {
                    SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false, cancellationToken);
                    await WaitForChangeAsync(cancellationToken);
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
            try
            {
                motor.Dispose();
            }
            finally
            {
                _repeat = false;
                _inspection.ClearInspectionRequest();
                EndRun(cancellationToken);
            }
        }
    }

    internal MainConveyorState GetNextStep(bool runCommandOn)
    {
        if (runCommandOn)
            return MainConveyorState.Running;
        if (_inspection.CarrierSeatingRequested)
            return MainConveyorState.WaitingForInspectionTransfer;
        // S1/S2 착좌는 벨트 이송보다 먼저 처리한다.
        if (_fastening.CarrierPresent && !_fastening.CarrierSeated
            || _placement.CarrierPresent && !_placement.CarrierSeated)
            return MainConveyorState.SeatingCarriers;

        var transfer = NextTransfer;
        if (!_inspection.Station.CarrierPresent)
            return transfer;
        if (_inspection.Station.Completed)
        {
            // 검사 완료: 바로 배출할 수 없으면 플레이트를 올려 벨트에서 분리한다.
            if (IsTransferPaused || _inspection.Station.CarrierSeated)
                return transfer;
            if (!_repeat
                && !IsNgTransferRequired
                && _inspection.IsTransferAllowed
                && DownstreamReady)
                return _inspection.IsRearDischargeReady
                    ? MainConveyorState.DischargingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
        }
        // 검사 전에는 S3를 올려 다른 물류를 먼저 처리한다.
        else if (!_inspection.InspectionRequested
            && transfer is MainConveyorState.DischargingInspectionCarrier
                or MainConveyorState.MovingPcbPlacementToBoltFastening
                or MainConveyorState.ReceivingFrontCarrier)
        {
            if (_inspection.Station.CarrierSeated)
                return transfer;
        }
        else
        {
            // 검사 요청 이후에는 검사와 전용 대기 위치 복귀가 끝날 때까지 벨트를 정지한다.
            if (_inspection.InspectionRequested && _inspection.IsAtInspectionPosition)
                return MainConveyorState.WaitingForInspection;
            return _inspection.IsClear
                ? MainConveyorState.PreparingInspectionCarrier
                : MainConveyorState.WaitingForInspectionTransfer;
        }
        return _inspection.IsClear && _units.Inspection
            ? MainConveyorState.RaisingInspectionCarrier
            : MainConveyorState.WaitingForInspectionTransfer;
    }

    private MainConveyorState NextTransfer
    {
        get
        {
            // 이송 우선순위: S3 배출 → S2→S3 → S1→S2 → 전단 반입.
            if (!IsTransferPaused)
            {
                if (IsRearDischargeAllowed && DownstreamReady)
                    return MainConveyorState.DischargingInspectionCarrier;
                if ((!_repeat || ReferenceEquals(RepeatEndStation, _inspection.Station))
                    && _fastening.Completed && _inspection.IsReceiveAllowed)
                    return MainConveyorState.MovingBoltFasteningToInspection;
                if ((!_repeat || !ReferenceEquals(RepeatEndStation, _placement))
                    && _placement.Completed && !_fastening.CarrierPresent)
                    return MainConveyorState.MovingPcbPlacementToBoltFastening;
                if (!_placement.CarrierPresent
                    && (Io.GetInput(InputIo.MainConveyorEntryCarrierDetected) || !_repeat && UpstreamCarrierAvailable))
                    return MainConveyorState.ReceivingFrontCarrier;
            }
            if (IsRearDischargeAllowed)
                return MainConveyorState.WaitingForRearEquipment;
            if (_fastening.CarrierPresent)
                return _fastening.Completed
                    ? MainConveyorState.WaitingForInspectionClear
                    : MainConveyorState.WaitingForBoltFastening;
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
        if (!_placement.CarrierPresent && _placement.BackupPlate != StationCylinderState.Down)
            preparation.Add(Io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementBackupPlateUp, false, cancellationToken));
        if (!_fastening.CarrierPresent && _fastening.BackupPlate != StationCylinderState.Down)
            preparation.Add(Io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningBackupPlateUp, false, cancellationToken));
        if (_inspection.IsReceiveAllowed && _inspection.Station.BackupPlate != StationCylinderState.Down)
            preparation.Add(Io.SetOutputAndWaitAsync(
                OutputIo.InspectionBackupPlateUp, false, cancellationToken));
        return Task.WhenAll(preparation);
    }

    private async Task TransferAsync(
        ConveyorStation? source,
        ConveyorStation destination,
        CancellationToken cancellationToken)
    {
        var departingJob = source?.CurrentJob;
        var receiving = source is null;
        var timeout = TimeSpan.FromSeconds(_settings.TransferTimeoutSeconds);
        var timeoutMilliseconds = (int)timeout.TotalMilliseconds;
        var arrived = new TaskCompletionSource<ConveyorStation.Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void ObserveEntry(InputIo input, bool value)
        {
            // The base I/O notification may wake the loop before this subscriber records entry.
            if (input == InputIo.MainConveyorEntryCarrierDetected && value && entered.TrySetResult())
                WakeRun();
        }
        void ObserveArrival()
        {
            if (destination.BackupPlate != StationCylinderState.Down
                || destination.Stopper != StationCylinderState.Up)
                OperationCancellation.CancelIfNotDisposed(transfer);
            if (destination.IsHeatSinkPresent(HeatSinkSlot.HeatSink2))
                arrived.TrySetResult(destination.CurrentJob);
        }
        Exception? failure = null;
        try
        {
            using var motor = new ConveyorRun(Io, OutputIo.MainConveyorRun, transfer.Token);
            try
            {
                if (!receiving)
                    SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!receiving)
                    destination.ClearJob();
                // 목적지가 준비될 때까지 출발 캐리어는 벨트에서 분리해 둔다.
                await destination.PrepareToReceiveAsync(cancellationToken);
                RequireSeatingPushPosition(destination);
                if (source is not null)
                {
                    source.RequireCurrentJob(departingJob!);
                    if (!source.CarrierSeated
                        || !source.Completed
                        || !(ReferenceEquals(destination, _inspection.Station)
                            ? _inspection.IsReceiveAllowed : !destination.CarrierPresent))
                    {
                        throw new InvalidOperationException(
                            "Transfer requires the completed source carrier to remain seated and the destination to remain empty.");
                    }
                    await source.ReleaseAsync(cancellationToken);
                    RequireSeatingPushPosition(destination);
                }

                if (receiving)
                    Io.InputChanged += ObserveEntry;
                if (receiving && Io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
                    entered.TrySetResult();
                if (receiving)
                {
                    if (IsTransferPaused && !entered.Task.IsCompleted)
                    {
                        SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false, transfer.Token);
                        return;
                    }
                    // Arm entry detection before READY; this call owns the whole receipt.
                    if (!_repeat && !entered.Task.IsCompleted)
                        SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, true, transfer.Token);
                    while (!entered.Task.IsCompleted && !UpstreamCarrierAvailable)
                    {
                        if (IsTransferPaused)
                        {
                            SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false, transfer.Token);
                            return;
                        }
                        var next = GetNextStep(false);
                        if (next is not (MainConveyorState.WaitingForFrontCarrier
                            or MainConveyorState.WaitingForRearEquipment
                            or MainConveyorState.WaitingForBoltFastening
                            or MainConveyorState.WaitingForInspectionClear
                            or MainConveyorState.ReceivingFrontCarrier))
                        {
                            SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false, transfer.Token);
                            return;
                        }
                        EnterStep(next, waitingFor: "Entry carrier detected=ON OR Front 2 Available=ON");
                        // No receipt has started: a completed S3 may now advertise its carrier.
                        SetSmemaOutput(OutputIo.MainConveyorAvailableToRear, !IsTransferPaused && IsRearDischargeAllowed, transfer.Token);
                        await WaitForChangeAsync(transfer.Token);
                    }
                    // READY waiting alone does not replace the station's work.
                    destination.ClearJob();
                    EnterStep(MainConveyorState.ReceivingFrontCarrier, waitingFor: "S1 Heat Sink 2 detected=ON");
                }
                destination.Changed += ObserveArrival;
                ObserveArrival();
                StartMotor(transfer.Token);
                try
                {
                    await arrived.Task.WaitAsync(timeout, transfer.Token);
                }
                catch (TimeoutException)
                {
                    throw new IoTimeoutException(destination.HeatSink2Input, true, timeoutMilliseconds);
                }
                if (Step is MainConveyorState step)
                    TraceStep(step, target: "seating push", workId: destination.CurrentJob.Id, waitingFor:
                        $"Heat Sink 2 detected; push for {_settings.CarrierStopDelaySeconds} s");
                // HS2 감지 뒤에는 순간적인 감지 끊김과 관계없이 밀착 시간을 채운다.
                await Task.Delay(
                    TimeSpan.FromSeconds(_settings.CarrierStopDelaySeconds),
                    transfer.Token);
                transfer.Token.ThrowIfCancellationRequested();
                if (!destination.CarrierPresent)
                    throw new InvalidOperationException("Carrier was not detected after the seating push.");
            }
            catch (OperationCanceledException exception) when (transfer.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                motor.Failure = cancellationToken.IsCancellationRequested
                    ? exception
                    : new MotionInterlockException(
                        "Transfer lost backup plate DOWN and stopper UP feedback. Check the stopped carrier position.");
            }
            catch (Exception exception)
            {
                motor.Failure = exception;
            }
            finally
            {
                if (receiving)
                    Io.InputChanged -= ObserveEntry;
                destination.Changed -= ObserveArrival;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            // The belt is stopped before publishing results, including a STOP after arrival.
            try
            {
                // HS2로 도착이 확인된 작업은 밀착 중 STOP해도 체결 결과를 이어받는다.
                // 목적지 작업이 별도로 초기화되었으면 이전 결과를 넘기지 않는다.
                if (source is not null
                    && arrived.Task.IsCompletedSuccessfully
                    && destination.CarrierPresent
                    && ReferenceEquals(destination.CurrentJob, await arrived.Task))
                {
                    source.TransferAssembliesTo(destination, departingJob!, await arrived.Task);
                }
            }
            catch (Exception handoffFailure) when (failure is not null)
            {
                throw new AggregateException(failure, handoffFailure);
            }
        }

        // S3는 플레이트 DOWN, 스토퍼 UP 상태에서 검사한다.
        if (!ReferenceEquals(destination, _inspection.Station))
            await destination.SeatAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (receiving)
            SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false, cancellationToken);
    }

    private static void RequireSeatingPushPosition(ConveyorStation destination)
    {
        if (destination.BackupPlate != StationCylinderState.Down
            || destination.Stopper != StationCylinderState.Up)
        {
            throw new InvalidOperationException(
                "Seating push requires backup plate DOWN and stopper UP feedback. Check the stopped carrier position.");
        }
    }

    private void SetSmemaOutput(OutputIo output, bool value, CancellationToken cancellationToken)
    {
        // The selector is ON in teaching/manual mode; direct OUTPUTS remain available.
        if (!Io.GetInput(InputIo.AutoMode))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Io.SetOutput(output, value);
        }
    }

    public async Task RunMotorAsync(CancellationToken cancellationToken = default)
    {
        using var runCancellation = BeginConveyorOperation(cancellationToken);
        cancellationToken = runCancellation.Token;
        using var motor = new ConveyorRun(Io, OutputIo.MainConveyorRun, cancellationToken, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
        try
        {
            StartMotor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
    }

    private OperationCancellation.Operation BeginConveyorOperation(CancellationToken cancellationToken)
    {
        Stop();
        var operation = _operations.Link(cancellationToken);
        _runCancellation = operation;
        operation.Disposed += () =>
        {
            if (ReferenceEquals(_runCancellation, operation))
                _runCancellation = null;
        };
        return operation;
    }

    public void Stop()
    {
        var run = _runCancellation;
        _runCancellation = null;
        using var motor = new ConveyorRun(
            Io, OutputIo.MainConveyorRun, CancellationToken.None,
            OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
        try
        {
            run?.Cancel();
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
    }

    private void StartMotor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        Io.SetOutput(OutputIo.MainConveyorForward, !reverse);
        cancellationToken.ThrowIfCancellationRequested();
        Io.SetOutput(OutputIo.MainConveyorRun, true);
    }
}
