using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed class MainConveyor : AutoUnit
{
    private readonly IIoService _io;
    private readonly ConveyorSettings _settings;
    private readonly OperationCancellation _operations;
    private readonly StationWork _placementWork;
    private readonly StationWork _boltFasteningWork;
    private readonly StationWork _inspectionWork;
    private readonly ConveyorStation _placement;
    private readonly ConveyorStation _boltFastening;
    private readonly ConveyorStation _inspection;
    private readonly Func<bool> _routeInspectionToNg;
    private OperationCancellation.Operation? _runCancellation;
    // An interrupted automatic move needs operator acknowledgement, never step replay.
    private volatile bool _requiresManualClear;
    private StationWork.Job? _transferJob;
    // The command currently being awaited, not a physical position or a resumable phase.
    private volatile MainConveyorState _executingTransfer = MainConveyorState.Idle;
    private bool _repeat;
    // Commissioning inputs, kept only for this application session.
    private volatile bool _testUpstreamCarrierAvailable;
    private volatile bool _testDownstreamReady;

    public MainConveyor(
        IIoService io,
        ConveyorSettings settings,
        OperationCancellation operations,
        StationWork placementWork,
        StationWork boltFasteningWork,
        StationWork inspectionWork,
        Func<bool> routeInspectionToNg)
    {
        _io = io;
        _settings = settings;
        _operations = operations;
        _placementWork = placementWork;
        _boltFasteningWork = boltFasteningWork;
        _inspectionWork = inspectionWork;
        _placement = placementWork.Station;
        _boltFastening = boltFasteningWork.Station;
        _inspection = inspectionWork.Station;
        _routeInspectionToNg = routeInspectionToNg;
        io.InputChanged += OnInputChanged;
        placementWork.Changed += NotifyChanged;
        boltFasteningWork.Changed += NotifyChanged;
        inspectionWork.Changed += NotifyChanged;
    }

    public override event Action? Changed;

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
        get
        {
            return _testUpstreamCarrierAvailable;
        }
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
        get
        {
            return _testDownstreamReady;
        }
        set
        {
            value = value && _io.IsReady && _io.GetInput(InputIo.AutoMode);
            if (_testDownstreamReady == value)
                return;
            _testDownstreamReady = value;
            Changed?.Invoke();
        }
    }

    public bool RunCommandOn
    {
        get
        {
            return _io.GetOutput(OutputIo.MainConveyorRun);
        }
    }

    public bool EntryCarrierDetected
    {
        get
        {
            return _io.GetInput(InputIo.MainConveyorEntryCarrierDetected);
        }
    }

    public bool ExitCarrierDetected
    {
        get
        {
            return _io.GetInput(InputIo.MainConveyorExitCarrierDetected);
        }
    }

    public int CarrierCount
    {
        get
        {
            return (EntryCarrierDetected ? 1 : 0)
                + (_placement.CarrierPresent ? 1 : 0)
                + (_boltFastening.CarrierPresent ? 1 : 0)
                + (_inspection.CarrierPresent ? 1 : 0)
                + (ExitCarrierDetected ? 1 : 0);
        }
    }

    public MainConveyorState State
    {
        get
        {
            return ReadState(RunCommandOn);
        }
    }

    public MainConveyorState ReadState(bool runCommandOn)
    {
        if (RequiresManualClear)
            return MainConveyorState.ManualClearRequired;
        var executingTransfer = _executingTransfer;
        if (executingTransfer != MainConveyorState.Idle)
            return executingTransfer;
        if (runCommandOn)
            return MainConveyorState.Running;

        if (_inspectionWork.CarrierPresent && !_inspectionWork.CarrierSeated)
        {
            return MainConveyorState.SeatingInspectionCarrier;
        }

        if (_boltFasteningWork.CarrierPresent && !_boltFasteningWork.CarrierSeated)
        {
            return MainConveyorState.SeatingBoltFasteningCarrier;
        }

        if (_placementWork.CarrierPresent
            && !_placementWork.CarrierSeated)
        {
            return MainConveyorState.SeatingPcbPlacementCarrier;
        }

        if (ExitCarrierDetected)
        {
            return DownstreamReady
                ? MainConveyorState.DischargingInspectionCarrier
                : MainConveyorState.WaitingForRearEquipment;
        }

        if (CanDischargeInspection)
        {
            return MainConveyorState.DischargingInspectionCarrier;
        }

        if (CanMoveBoltFasteningToInspection)
        {
            return MainConveyorState.MovingBoltFasteningToInspection;
        }

        if (CanMovePlacementToBoltFastening)
        {
            return MainConveyorState.MovingPcbPlacementToBoltFastening;
        }

        if (CanReceiveAtPlacement)
        {
            return MainConveyorState.ReceivingFrontCarrier;
        }

        if (CanOfferToRear)
        {
            return MainConveyorState.WaitingForRearEquipment;
        }

        return _placementWork.CarrierPresent
            ? MainConveyorState.Idle
            : MainConveyorState.WaitingForFrontCarrier;
    }

    public Task RunMotorAsync(CancellationToken cancellationToken = default)
    {
        return RunControlledAsync(
            async token =>
            {
                StartMotor(token);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        _repeat = repeat;
        try
        {
            await RunControlledAsync(token => RunLoopAsync(ExecuteAsync, token), cancellationToken);
        }
        finally
        {
            _repeat = false;
        }
    }

    public async Task ReturnToStartAsync(CancellationToken cancellationToken)
    {
        if (RequiresManualClear)
            throw new InvalidOperationException("Clear the main conveyor manually, then press RESET before starting a new move.");
        if (CarrierCount > 1 || ExitCarrierDetected)
            throw new InvalidOperationException("Main conveyor return requires one carrier and a clear exit.");

        _repeat = true;
        try
        {
            await RunControlledAsync(ReturnCarrierAsync, cancellationToken);
        }
        catch
        {
            _requiresManualClear = true;
            Changed?.Invoke();
            throw;
        }
        finally
        {
            _repeat = false;
        }
    }

    private async Task ReturnCarrierAsync(CancellationToken cancellationToken)
    {
        if (!EntryCarrierDetected
            && !_placement.CarrierPresent
            && !_boltFastening.CarrierPresent
            && !_inspection.CarrierPresent)
        {
            throw new InvalidOperationException("Return carrier position is unknown. Restore carrier presence before restarting.");
        }

        await Task.WhenAll(
            _placement.ReleaseAsync(cancellationToken),
            _boltFastening.ReleaseAsync(cancellationToken),
            _inspection.ReleaseAsync(cancellationToken));

        if (EntryCarrierDetected)
            return;

        Exception? failure = null;
        try
        {
            StartMotor(cancellationToken, reverse: true);
            await _io.WaitForInputAsync(
                InputIo.MainConveyorEntryCarrierDetected,
                true,
                cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure, OutputIo.MainConveyorRun);
        }
    }

    internal async Task RunControlledAsync(
        Func<CancellationToken, Task> run,
        CancellationToken cancellationToken)
    {
        Stop();
        using var runCancellation = _operations.Link(cancellationToken);
        _runCancellation = runCancellation;
        cancellationToken = runCancellation.Token;
        Exception? cancellationFailure = null;
        void StopOnCancellation()
        {
            try
            {
                _io.SetOutput(OutputIo.MainConveyorRun, false);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
        }

        Exception? failure = null;
        try
        {
            using (cancellationToken.Register(StopOnCancellation))
            {
                try
                {
                    await run(cancellationToken);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            if (cancellationFailure is not null)
                failure = failure is null ? cancellationFailure : new AggregateException(failure, cancellationFailure);
            if (failure is not null)
                ExceptionDispatchInfo.Throw(failure);
        }
        finally
        {
            try
            {
                StopOutputs(failure,
                    OutputIo.MainConveyorRun,
                    OutputIo.MainConveyorReadyToFront2,
                    OutputIo.MainConveyorAvailableToRear);
            }
            finally
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                    _runCancellation = null;
            }
        }
    }

    public Task PrepareEmptyStationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RequiresManualClear)
            throw new InvalidOperationException("Automatic conveyor move was interrupted. Clear all carriers manually, then press RESET. START cannot resume the move.");

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
        TraceStep(state, workId: _transferJob?.Id, waitingFor: state switch
        {
            MainConveyorState.WaitingForFrontCarrier => "Front 2 Available=ON (teaching: TEST, auto: DI)",
            MainConveyorState.WaitingForRearEquipment => "Rear Ready=ON (teaching: TEST, auto: DI)",
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
                case MainConveyorState.SeatingInspectionCarrier:
                case MainConveyorState.SeatingBoltFasteningCarrier:
                case MainConveyorState.SeatingPcbPlacementCarrier:
                    // The belt is stopped. Each occupied station can lift and start
                    // its own work without waiting for another station to finish.
                    var seating = new List<Task>(3);
                    if (_inspectionWork.CarrierPresent && !_inspectionWork.CarrierSeated)
                        seating.Add(_inspection.SeatAsync(cancellationToken));
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
        catch when (state is MainConveyorState.SeatingInspectionCarrier
            or MainConveyorState.SeatingBoltFasteningCarrier
            or MainConveyorState.SeatingPcbPlacementCarrier
            or MainConveyorState.ReceivingFrontCarrier
            or MainConveyorState.MovingPcbPlacementToBoltFastening
            or MainConveyorState.MovingBoltFasteningToInspection
            or MainConveyorState.DischargingInspectionCarrier)
        {
            _requiresManualClear = true;
            Changed?.Invoke();
            throw;
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

    public bool RequiresManualClear
    {
        get
        {
            return _requiresManualClear;
        }
    }

    // RESET acknowledges the operator's manual removal, including carriers between sensors.
    // Empty inputs alone cannot establish that an interrupted transfer left no carrier behind.
    public void ConfirmManualClear()
    {
        if (!RequiresManualClear)
            return;
        _io.CheckReady();
        if (_runCancellation is not null
            || RunCommandOn
            || CarrierCount != 0
            || !_inspectionWork.CanReceive)
        {
            throw new InvalidOperationException("Stop and manually empty the main conveyor before RESET. Check between sensors and remove any carrier held at Station 3.");
        }

        TraceStep(MainConveyorState.ManualClearRequired,
            target: "operator confirmed empty conveyor; interrupted move abandoned",
            workId: _transferJob?.Id);
        _transferJob = null;
        _requiresManualClear = false;
        Changed?.Invoke();
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

    private bool CanOfferToRear
    {
        get
        {
            return ExitCarrierDetected
                || !_repeat && !_routeInspectionToNg()
                && _inspectionWork.CanTransfer;
        }
    }

    private bool CanDischargeInspection
    {
        get
        {
            return CanOfferToRear && DownstreamReady;
        }
    }

    private bool CanMoveBoltFasteningToInspection
    {
        get
        {
            return _boltFasteningWork.CanTransfer && _inspectionWork.CanReceive;
        }
    }

    private bool CanMovePlacementToBoltFastening
    {
        get
        {
            return _placementWork.CanTransfer && _boltFasteningWork.CanReceive;
        }
    }

    private bool CanReceiveAtPlacement
    {
        get
        {
            return _placementWork.CanReceive
                && (!_repeat && UpstreamCarrierAvailable
                    || EntryCarrierDetected);
        }
    }

    private void UpdateSmema()
    {
        var rearAvailable = CanOfferToRear;
        _io.SetAutomaticSmemaOutput(
            OutputIo.MainConveyorReadyToFront2,
            !_repeat && _placementWork.CanReceive && !rearAvailable);
        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, rearAvailable);
    }

    private async Task ReceiveAtPlacementAsync(CancellationToken cancellationToken)
    {
        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, false);
        Exception? failure = null;
        try
        {
            await _placement.PrepareToReceiveAsync(cancellationToken);
            RequireSeatingPushPosition(_placement);
            if (!_repeat && !EntryCarrierDetected)
                _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, true);

            await RunToStationAsync(_placementWork, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorReadyToFront2);
        }

        if (!_placement.CarrierPresent)
            throw new InvalidOperationException("Carrier presence was lost after the seating push.");
        await _placement.SeatAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task MoveCarrierAsync(
        StationWork sourceWork,
        StationWork destinationWork,
        CancellationToken cancellationToken)
    {
        var source = sourceWork.Station;
        var destination = destinationWork.Station;
        var departingJob = sourceWork.CurrentJob;
        _transferJob = departingJob;
        void TransferArrivingWork(bool present)
        {
            if (present
                && !cancellationToken.IsCancellationRequested
                && Interlocked.Exchange(ref _transferJob, null) is { } job)
            {
                sourceWork.TransferAssembliesTo(destinationWork, job);
            }
        }
        Exception? failure = null;
        try
        {
            StopOutputs(null, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
            // Keep the carrier off the belt until its destination is ready.
            await destination.PrepareToReceiveAsync(cancellationToken);
            RequireSeatingPushPosition(destination);
            sourceWork.RequireCurrentJob(departingJob);
            if (!sourceWork.CarrierSeated
                || !sourceWork.CanTransfer
                || !destinationWork.CanReceive)
            {
                throw new InvalidOperationException(
                    "Transfer requires the completed source carrier to remain seated and the destination to remain empty.");
            }
            destination.CarrierChanged += TransferArrivingWork;
            await source.ReleaseAsync(cancellationToken);
            RequireSeatingPushPosition(destination);
            await RunToStationAsync(destinationWork, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            destination.CarrierChanged -= TransferArrivingWork;
            StopOutputs(failure, OutputIo.MainConveyorRun);
        }

        if (!destination.CarrierPresent)
            throw new InvalidOperationException("Carrier presence was lost before raising the backup plate.");
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

    private async Task RunToStationAsync(
        StationWork destinationWork,
        CancellationToken cancellationToken)
    {
        var destination = destinationWork.Station;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var carrierLeft = new AsyncAutoResetEvent();
        var receiving = ReferenceEquals(destination, _placement);
        void ObserveEntry(InputIo input, bool value)
        {
            if (input == InputIo.MainConveyorEntryCarrierDetected && value)
                entered.TrySetResult();
        }
        void ObserveArrival()
        {
            if (destinationWork.HeatSinkPresent(HeatSinkSlot.HeatSink2))
                arrived.TrySetResult();
            if (arrived.Task.IsCompleted && !destination.CarrierPresent)
                carrierLeft.Set();
        }
        destination.Changed += ObserveArrival;
        if (receiving)
            _io.InputChanged += ObserveEntry;
        try
        {
            ObserveArrival();
            if (receiving && EntryCarrierDetected)
                entered.TrySetResult();
            StartMotor(cancellationToken);
            if (receiving)
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromMilliseconds(_io.TimeoutMilliseconds), cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new IoTimeoutException(InputIo.MainConveyorEntryCarrierDetected, true, _io.TimeoutMilliseconds);
                }
                _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            }
            await arrived.Task.WaitAsync(cancellationToken);
            if (!destination.CarrierPresent)
                carrierLeft.Set();
            TraceStep(State, target: "seating push", workId: destinationWork.CurrentJob.Id, waitingFor:
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
        finally
        {
            if (receiving)
                _io.InputChanged -= ObserveEntry;
            destination.Changed -= ObserveArrival;
        }
    }

    private async Task DischargeInspectionAsync(CancellationToken cancellationToken)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var departed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveExit(InputIo input, bool value)
        {
            if (input != InputIo.MainConveyorExitCarrierDetected)
                return;
            if (value)
                arrived.TrySetResult();
            else if (arrived.Task.IsCompleted)
                departed.TrySetResult();
        }

        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, true);
        _io.InputChanged += ObserveExit;
        Exception? failure = null;
        try
        {
            if (ExitCarrierDetected)
                arrived.TrySetResult();
            if (_inspectionWork.CarrierPresent)
            {
                await _inspection.ReleaseAsync(cancellationToken);
            }

            StartMotor(cancellationToken);
            var timeout = TimeSpan.FromMilliseconds(_io.TimeoutMilliseconds);
            try
            {
                await arrived.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new IoTimeoutException(InputIo.MainConveyorExitCarrierDetected, true, _io.TimeoutMilliseconds);
            }
            try
            {
                await departed.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new IoTimeoutException(InputIo.MainConveyorExitCarrierDetected, false, _io.TimeoutMilliseconds);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _io.InputChanged -= ObserveExit;
            StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorAvailableToRear);
        }
    }

    private void StartMotor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorForward, !reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorRun, true);
    }

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
            or InputIo.MainConveyorEntryCarrierDetected
            or InputIo.MainConveyorExitCarrierDetected)
        {
            Changed?.Invoke();
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
