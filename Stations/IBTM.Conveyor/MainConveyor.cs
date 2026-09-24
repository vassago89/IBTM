using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor : AutoUnit
{
    private readonly IIoService _io;
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
    {
        _io = io;
        _settings = settings;
        _operations = operations;
        _placement = placement;
        _fastening = fastening;
        _inspection = inspection;
        _units = units;
        io.InputChanged += OnInputChanged;
        placement.Changed += NotifyChanged;
        fastening.Changed += NotifyChanged;
        inspection.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    public MainConveyorState State => Step is MainConveyorState step ? step : GetNextStep(RunCommandOn);

    private bool IsNgTransferRequired => _units.Inspection && _inspection.RouteToNg;

    public bool UpstreamCarrierAvailable
    {
        get
        {
            return _io.GetInput(InputIo.AutoMode)
                ? _testUpstreamCarrierAvailable
                : _io.GetInput(InputIo.MainConveyorAvailableFromFront2);
        }
    }

    public bool DownstreamReady
    {
        get
        {
            return _io.GetInput(InputIo.AutoMode)
                ? _testDownstreamReady
                : _io.GetInput(InputIo.MainConveyorReadyFromRear);
        }
    }

    public bool TestUpstreamCarrierAvailable
    {
        get => _testUpstreamCarrierAvailable;
        set
        {
            // The selector contact is ON in teaching/manual mode.
            value = value && _io.IsReady && _io.GetInput(InputIo.AutoMode);
            if (_testUpstreamCarrierAvailable == value)
                return;
            _testUpstreamCarrierAvailable = value;
            Changed?.Invoke();
        }
    }

    public bool TestDownstreamReady
    {
        get => _testDownstreamReady;
        set
        {
            value = value && _io.IsReady && _io.GetInput(InputIo.AutoMode);
            if (_testDownstreamReady == value)
                return;
            _testDownstreamReady = value;
            Changed?.Invoke();
        }
    }

    public bool RunCommandOn => _io.GetOutput(OutputIo.MainConveyorRun);

    public bool EntryCarrierDetected => _io.GetInput(InputIo.MainConveyorEntryCarrierDetected);

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.AutoMode && !value)
        {
            _testUpstreamCarrierAvailable = false;
            _testDownstreamReady = false;
        }

        if (input is InputIo.AutoMode
            or InputIo.MainConveyorAvailableFromFront2
            or InputIo.MainConveyorReadyFromRear
            or InputIo.MainConveyorEntryCarrierDetected)
        {
            Changed?.Invoke();
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

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
                        return _inspection.IsClear
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
                return _inspection.IsClear
                    && _units.IsMotionEnabled(MotionGroup.InspectionGantry)
                    ? MainConveyorState.RaisingInspectionCarrier
                    : MainConveyorState.WaitingForInspectionTransfer;
            // 검사 요청 이후에는 검사와 전용 대기 위치 복귀가 끝날 때까지 벨트를 정지한다.
            case true when _inspection.InspectionRequested && _inspection.IsAtInspectionPosition(live ? null : runCommandOn):
                return MainConveyorState.WaitingForInspection;
            default:
                return _inspection.IsClear
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
        var carrierLeft = new AsyncAutoResetEvent();
        void ObserveEntry(InputIo input, bool value)
        {
            if (input == InputIo.MainConveyorEntryCarrierDetected && value)
                entered.TrySetResult();
        }
        void ObserveArrival()
        {
            if (destination.IsHeatSinkPresent(HeatSinkSlot.HeatSink2))
                arrived.TrySetResult(destination.CurrentJob);
            if (arrived.Task.IsCompleted && !destination.CarrierPresent)
                carrierLeft.Set();
        }
        Exception? failure = null;
        try
        {
            StopOutputs(null, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
            // 목적지가 준비될 때까지 출발 캐리어는 벨트에서 분리해 둔다.
            await destination.PrepareToReceiveAsync(cancellationToken);
            RequireSeatingPushPosition(destination);
            if (source is not null)
            {
                source.RequireCurrentJob(departingJob!);
                if (!source.CarrierSeated
                    || !source.IsTransferAllowed
                    || !(ReferenceEquals(destination, _inspection.Station) ? _inspection.IsReceiveAllowed : destination.IsReceiveAllowed))
                {
                    throw new InvalidOperationException(
                        "Transfer requires the completed source carrier to remain seated and the destination to remain empty.");
                }
                await source.ReleaseAsync(cancellationToken);
                RequireSeatingPushPosition(destination);
            }
            else if (!_repeat && !EntryCarrierDetected)
            {
                SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, true);
            }

            destination.Changed += ObserveArrival;
            if (receiving)
                _io.InputChanged += ObserveEntry;
            ObserveArrival();
            if (receiving && EntryCarrierDetected)
                entered.TrySetResult();
            StartMotor(cancellationToken);
            if (receiving)
            {
                try
                {
                    await entered.Task.WaitAsync(timeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new IoTimeoutException(InputIo.MainConveyorEntryCarrierDetected, true, timeoutMilliseconds);
                }
                SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            }
            try
            {
                await arrived.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new IoTimeoutException(destination.HeatSink2Input, true, timeoutMilliseconds);
            }
            if (!destination.CarrierPresent)
                carrierLeft.Set();
            EnterStep(State, target: "seating push", workId: destination.CurrentJob.Id, waitingFor:
                $"Heat Sink 2 detected; push for {_settings.CarrierStopDelaySeconds} s");
            var lostCarrier = await carrierLeft.WaitAsync(
                TimeSpan.FromSeconds(_settings.CarrierStopDelaySeconds),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (lostCarrier || !destination.CarrierPresent)
            {
                throw new InvalidOperationException("Carrier presence was lost during the seating push.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (receiving)
                _io.InputChanged -= ObserveEntry;
            destination.Changed -= ObserveArrival;
            // Result notifications must not delay the physical end of the transfer.
            try
            {
                StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorReadyToFront2);
            }
            catch (Exception stopFailure)
            {
                failure = stopFailure;
            }
            try
            {
                // HS2로 도착이 확인된 작업은 밀착 중 STOP해도 체결 결과를 이어받는다.
                // 감지 후 교체된 캐리어에는 이전 결과를 넘기지 않는다.
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
            if (failure is not null)
                ExceptionDispatchInfo.Throw(failure);
        }

        // S3는 플레이트 DOWN, 스토퍼 UP 상태에서 검사한다.
        if (!ReferenceEquals(destination, _inspection.Station))
            await destination.SeatAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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

    private async Task DischargeInspectionAsync(CancellationToken cancellationToken)
    {
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
        Exception? failure = null;
        try
        {
            SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            SetSmemaOutput(OutputIo.MainConveyorAvailableToRear, true);
            ObserveRear();
            if (rearReleased.Task.IsCompleted)
                return;
            _inspection.Station.RequireCurrentJob(departingJob);
            if (_repeat
                || IsNgTransferRequired
                || !_inspection.IsTransferAllowed
                || !_inspection.IsTransferAtWaitingPosition())
            {
                throw new MotionInterlockException(
                    "Rear discharge requires a completed carrier and the raised, clear inspection pickup at its waiting position.");
            }
            await _inspection.Station.ReleaseAsync(cancellationToken);

            if (rearReleased.Task.IsCompleted)
                return;
            _inspection.Station.RequireCurrentJob(departingJob);
            if (_inspection.Station.BackupPlate != StationCylinderState.Down
                || _inspection.Station.Stopper != StationCylinderState.Down
                || !_inspection.IsTransferAtWaitingPosition())
            {
                throw new MotionInterlockException(
                    "Rear discharge lost support release or inspection pickup clearance before starting the belt.");
            }
            EnterStep(MainConveyorState.DischargingInspectionCarrier, waitingFor: "Rear Ready=OFF");
            StartMotor(cancellationToken);
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
            failure = exception;
            throw;
        }
        finally
        {
            Changed -= ObserveRear;
            StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorAvailableToRear);
        }
    }

    private void SetSmemaOutput(OutputIo output, bool value)
    {
        // The selector contact is ON in teaching/manual mode; direct OUTPUTS remain available.
        if (!_io.GetInput(InputIo.AutoMode))
            _io.SetOutput(output, value);
    }

    public async Task RunMotorAsync(CancellationToken cancellationToken = default)
    {
        using var runCancellation = BeginConveyorOperation(cancellationToken);
        cancellationToken = runCancellation.Token;
        using var motor = new ConveyorRun(_io, OutputIo.MainConveyorRun, cancellationToken, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
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
        Exception? failure = null;
        try
        {
            run?.Cancel();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure,
                OutputIo.MainConveyorRun,
                OutputIo.MainConveyorReadyToFront2,
                OutputIo.MainConveyorAvailableToRear);
        }
    }

    private void StopOutputs(Exception? operationFailure, params OutputIo[] outputs)
    {
        List<Exception>? failures = null;
        foreach (var output in outputs)
        {
            try
            {
                _io.SetOutput(output, false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null && operationFailure is not null)
            failures.Insert(0, operationFailure);
        if (failures?.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures is not null)
            throw new AggregateException("Main conveyor outputs could not all be stopped.", failures);
    }

    private void StartMotor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorForward, !reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorRun, true);
    }
}
