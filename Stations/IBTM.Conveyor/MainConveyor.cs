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
    private volatile ConveyorTransfer _transfer;
    private StationWork.Job? _transferJob;
    // An uninterrupted timed push, not a physical carrier/plate position.
    private bool _seatingPushCompleted;
    private bool _repeat;

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
        _placement.CarrierChanged += value => OnCarrierChanged(StationPosition.PcbPlacement, value);
        _boltFastening.CarrierChanged += value => OnCarrierChanged(StationPosition.BoltFastening, value);
        _inspection.CarrierChanged += value => OnCarrierChanged(StationPosition.Inspection, value);
    }

    public override event Action? Changed;
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

    public MainConveyorState State
    {
        get
        {
            return ReadState(RunCommandOn);
        }
    }

    public MainConveyorState ReadState(bool runCommandOn)
    {
        // The destination is transfer history, not proof of a carrier's location.
        // After a stop between sensors, wait for presence feedback before resuming.
        if (!runCommandOn
            && (_transfer switch
            {
                ConveyorTransfer.ReceivingAfterEntry =>
                    !EntryCarrierDetected && !_placementWork.CarrierPresent,
                ConveyorTransfer.PcbPlacementToBoltFastening =>
                    !_placementWork.CarrierPresent && !_boltFasteningWork.CarrierPresent,
                ConveyorTransfer.BoltFasteningToInspection =>
                    !_boltFasteningWork.CarrierPresent && !_inspectionWork.CarrierPresent,
                ConveyorTransfer.DischargingInspectionToExit =>
                    !_inspectionWork.CarrierPresent && !ExitCarrierDetected,
                _ => false,
            }))
        {
            return MainConveyorState.CarrierPositionUnknown;
        }

        if (ExitCarrierDetected)
        {
            return _io.GetInput(InputIo.MainConveyorReadyFromRear)
                ? MainConveyorState.DischargingInspectionCarrier
                : MainConveyorState.WaitingForRearEquipment;
        }

        // Finish the selected transfer before a new front arrival can replace its
        // destination and the production results that still belong to that transfer.
        switch (_transfer)
        {
            case ConveyorTransfer.DischargingInspectionFromExit:
                return MainConveyorState.DischargingInspectionCarrier;

            case ConveyorTransfer.DischargingInspectionToExit:
                return _io.GetInput(InputIo.MainConveyorReadyFromRear)
                    ? MainConveyorState.DischargingInspectionCarrier
                    : MainConveyorState.WaitingForRearEquipment;

            case ConveyorTransfer.ReceivingBeforeEntry:
            case ConveyorTransfer.ReceivingAfterEntry:
                return MainConveyorState.ReceivingFrontCarrier;

            case ConveyorTransfer.BoltFasteningToInspection:
                return MainConveyorState.MovingBoltFasteningToInspection;

            case ConveyorTransfer.PcbPlacementToBoltFastening:
                return MainConveyorState.MovingPcbPlacementToBoltFastening;
        }

        if (_placementWork.CanReceive && EntryCarrierDetected)
        {
            return MainConveyorState.ReceivingFrontCarrier;
        }

        if (_inspectionWork.CarrierPresent && !_inspectionWork.CarrierSeated)
        {
            return MainConveyorState.SeatingInspectionCarrier;
        }

        if (_boltFasteningWork.CarrierPresent && !_boltFasteningWork.CarrierSeated)
        {
            return MainConveyorState.SeatingBoltFasteningCarrier;
        }

        if (_placementWork.CarrierPresent
            && !_placementWork.CarrierSeated
            && !_repeat)
        {
            return MainConveyorState.SeatingPcbPlacementCarrier;
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
        var carriers = (EntryCarrierDetected ? 1 : 0)
            + (_placement.CarrierPresent ? 1 : 0)
            + (_boltFastening.CarrierPresent ? 1 : 0)
            + (_inspection.CarrierPresent ? 1 : 0);
        if (carriers > 1 || ExitCarrierDetected)
            throw new InvalidOperationException("Main conveyor return requires one carrier and a clear exit.");

        // Reverse travel does not transfer production results to stations it passes.
        _transfer = ConveyorTransfer.None;
        _transferJob = null;
        _repeat = true;
        try
        {
            await RunControlledAsync(ReturnCarrierAsync, cancellationToken);
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

        // TEMP: until the front sensor is installed, stop at Station 1 and keep its plate down.
        if (_placement.CarrierPresent)
            return;

        Exception? failure = null;
        try
        {
            StartMotor(cancellationToken, reverse: true);
            await _placement.WaitForCarrierAsync(cancellationToken);
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
        Exception? failure = null;
        try
        {
            using var stopRegistration = cancellationToken.Register(StopMotor);
            await run(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                StopOutputs(null,
                    OutputIo.MainConveyorRun,
                    OutputIo.MainConveyorReadyToFront2,
                    OutputIo.MainConveyorAvailableToRear);
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }

            if (ReferenceEquals(_runCancellation, runCancellation))
            {
                _runCancellation = null;
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    public Task PrepareEmptyStationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == MainConveyorState.CarrierPositionUnknown)
            return Task.CompletedTask;

        // Preserve the support under an interrupted placement/fastening operation.
        // Station 3 also stays supported while the pickup still detects a carrier.
        var preparation = new List<Task>(3);
        if (_placementWork.CanReceive && _placement.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementBackupPlateDown, true, cancellationToken));
        if (_boltFasteningWork.CanReceive && _boltFastening.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningBackupPlateDown, true, cancellationToken));
        if (_inspectionWork.CanReceive && _inspection.BackupPlate != StationCylinderState.Down)
            preparation.Add(_io.SetOutputAndWaitAsync(
                OutputIo.InspectionBackupPlateDown, true, cancellationToken));
        return Task.WhenAll(preparation);
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await PrepareEmptyStationsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var state = State;
        TraceStep(state, workId: _transferJob?.Id, waitingFor: state switch
        {
            MainConveyorState.WaitingForFrontCarrier => "MainConveyorAvailableFromFront2=ON",
            MainConveyorState.WaitingForRearEquipment => "MainConveyorReadyFromRear=ON",
            MainConveyorState.Idle => "station work complete and destination vacant",
            MainConveyorState.CarrierPositionUnknown => "confirm carrier position before resuming",
            _ => null,
        });
        switch (state)
        {
            case MainConveyorState.CarrierPositionUnknown:
                StopOutputs(null, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
                await WaitForChangeAsync(cancellationToken);
                break;
            case MainConveyorState.SeatingInspectionCarrier:
                await _inspection.SeatAsync(cancellationToken);
                break;
            case MainConveyorState.SeatingBoltFasteningCarrier:
                await _boltFastening.SeatAsync(cancellationToken);
                break;
            case MainConveyorState.SeatingPcbPlacementCarrier:
                await _placement.SeatAsync(cancellationToken);
                break;
            case MainConveyorState.DischargingInspectionCarrier:
                await DischargeInspectionAsync(cancellationToken);
                break;
            case MainConveyorState.MovingBoltFasteningToInspection:
                await MoveCarrierAsync(
                    ConveyorTransfer.BoltFasteningToInspection,
                    _boltFastening,
                    _inspection,
                    cancellationToken);
                break;
            case MainConveyorState.MovingPcbPlacementToBoltFastening:
                await MoveCarrierAsync(
                    ConveyorTransfer.PcbPlacementToBoltFastening,
                    _placement,
                    _boltFastening,
                    cancellationToken);
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

    private void StopMotor()
    {
        _io.SetOutput(OutputIo.MainConveyorRun, false);
    }

    private bool CanOfferToRear
    {
        get
        {
            return ExitCarrierDetected
                || InspectionDischargeActive
                || !_repeat && !_routeInspectionToNg()
                && _inspectionWork.CanTransfer;
        }
    }

    private bool CanDischargeInspection
    {
        get
        {
            return CanOfferToRear && _io.GetInput(InputIo.MainConveyorReadyFromRear);
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
            var placementReady = _repeat
                ? _placementWork.CarrierPresent
                : _placementWork.CanTransfer;
            return placementReady && _boltFasteningWork.CanReceive;
        }
    }

    private bool CanReceiveAtPlacement
    {
        get
        {
            return _placementWork.CanReceive
                && (!_repeat && _io.GetInput(InputIo.MainConveyorAvailableFromFront2)
                    || EntryCarrierDetected);
        }
    }

    private bool InspectionDischargeActive
    {
        get
        {
            return _transfer is ConveyorTransfer.DischargingInspectionToExit
                or ConveyorTransfer.DischargingInspectionFromExit;
        }
    }

    private void UpdateSmema()
    {
        var rearAvailable = CanOfferToRear;
        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, !_repeat && _placementWork.CanReceive && !rearAvailable);
        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, rearAvailable);
    }

    private async Task ReceiveAtPlacementAsync(CancellationToken cancellationToken)
    {
        if (_transfer is not ConveyorTransfer.ReceivingBeforeEntry
            and not ConveyorTransfer.ReceivingAfterEntry)
        {
            _seatingPushCompleted = false;
            _transfer = EntryCarrierDetected
                ? ConveyorTransfer.ReceivingAfterEntry
                : ConveyorTransfer.ReceivingBeforeEntry;
        }

        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
        Exception? failure = null;
        try
        {
            if (!_seatingPushCompleted)
            {
                if (!_placement.CarrierPresent)
                    await _placement.PrepareToReceiveAsync(cancellationToken);
                else
                    _transfer = ConveyorTransfer.ReceivingAfterEntry;

                RequireSeatingPushPosition(_placement);
                if (!_repeat && _transfer == ConveyorTransfer.ReceivingBeforeEntry)
                    _io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);

                StartMotor(cancellationToken);
                if (_transfer == ConveyorTransfer.ReceivingBeforeEntry)
                {
                    await _io.WaitForInputAsync(
                        InputIo.MainConveyorEntryCarrierDetected,
                        true,
                        cancellationToken);
                    _transfer = ConveyorTransfer.ReceivingAfterEntry;
                }

                _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
                await CompleteSeatingPushAsync(_placement, cancellationToken);
            }
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

        _transfer = ConveyorTransfer.None;
    }

    private async Task MoveCarrierAsync(
        ConveyorTransfer transfer,
        ConveyorStation source,
        ConveyorStation destination,
        CancellationToken cancellationToken)
    {
        if (_transfer != transfer)
        {
            _seatingPushCompleted = false;
            _transferJob = transfer == ConveyorTransfer.PcbPlacementToBoltFastening
                ? _placementWork.CurrentJob
                : _boltFasteningWork.CurrentJob;
        }
        _transfer = transfer;
        TraceStep(transfer, workId: _transferJob?.Id, waitingFor:
            transfer == ConveyorTransfer.PcbPlacementToBoltFastening
                ? "BoltFasteningCarrierPresent=ON"
                : "InspectionCarrierPresent=ON");
        StopOutputs(null, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
        if (!_seatingPushCompleted)
        {
            if (!destination.CarrierPresent)
            {
                await Task.WhenAll(
                    source.ReleaseAsync(cancellationToken),
                    destination.PrepareToReceiveAsync(cancellationToken));
            }
            RequireSeatingPushPosition(destination);
            // A new front carrier must be caught while the interrupted transfer finishes.
            if (_placementWork.CanReceive && EntryCarrierDetected)
            {
                await _placement.PrepareToReceiveAsync(cancellationToken);
            }

            Exception? failure = null;
            try
            {
                StartMotor(cancellationToken);
                await CompleteSeatingPushAsync(destination, cancellationToken);
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

        if (!destination.CarrierPresent)
            throw new InvalidOperationException("Carrier presence was lost before raising the backup plate.");
        await destination.SeatAsync(cancellationToken);
        _transfer = ConveyorTransfer.None;
    }

    private static void RequireSeatingPushPosition(ConveyorStation destination)
    {
        // A stopped carrier may have been lifted manually. Do not lower its support
        // just to replay an unfinished belt push.
        if (destination.BackupPlate != StationCylinderState.Down
            || destination.Stopper != StationCylinderState.Up)
        {
            throw new InvalidOperationException(
                "Seating push requires backup plate DOWN and stopper UP feedback. Check the stopped carrier position.");
        }
    }

    private async Task CompleteSeatingPushAsync(
        ConveyorStation destination,
        CancellationToken cancellationToken)
    {
        await destination.WaitForCarrierAsync(cancellationToken, Timeout.Infinite);
        using var presence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        destination.CarrierChanged += StopWhenCarrierLeaves;
        try
        {
            if (!destination.CarrierPresent)
                presence.Cancel();
            // Arrival starts the push duration. STOP leaves it unfinished; resume
            // requires the full uninterrupted duration before raising the plate.
            await Task.Delay(TimeSpan.FromSeconds(_settings.CarrierStopDelaySeconds), presence.Token);
            presence.Token.ThrowIfCancellationRequested();
            _seatingPushCompleted = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Carrier presence was lost during the seating push.");
        }
        finally
        {
            destination.CarrierChanged -= StopWhenCarrierLeaves;
        }

        void StopWhenCarrierLeaves(bool present)
        {
            if (!present)
                presence.Cancel();
        }
    }

    private async Task DischargeInspectionAsync(CancellationToken cancellationToken)
    {
        if (!InspectionDischargeActive)
        {
            _transfer = ExitCarrierDetected
                ? ConveyorTransfer.DischargingInspectionFromExit
                : ConveyorTransfer.DischargingInspectionToExit;
        }

        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(
            OutputIo.MainConveyorAvailableToRear,
            _transfer == ConveyorTransfer.DischargingInspectionToExit
                || ExitCarrierDetected);
        Exception? failure = null;
        try
        {
            if (_inspectionWork.CarrierPresent)
            {
                await _inspection.ReleaseAsync(cancellationToken);
            }

            if (_transfer == ConveyorTransfer.DischargingInspectionToExit
                || ExitCarrierDetected)
            {
                StartMotor(cancellationToken);
                if (_transfer == ConveyorTransfer.DischargingInspectionToExit)
                {
                    await _io.WaitForInputAsync(
                        InputIo.MainConveyorExitCarrierDetected,
                        true,
                        cancellationToken);
                    _transfer = ConveyorTransfer.DischargingInspectionFromExit;
                }

                await _io.WaitForInputAsync(
                    InputIo.MainConveyorExitCarrierDetected,
                    false,
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorAvailableToRear);
        }

        _transfer = ConveyorTransfer.None;
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
        if (input == InputIo.MainConveyorEntryCarrierDetected
            && value
            && _transfer == ConveyorTransfer.ReceivingBeforeEntry)
        {
            _transfer = ConveyorTransfer.ReceivingAfterEntry;
        }

        if (input == InputIo.MainConveyorExitCarrierDetected
            && value
            && _transfer == ConveyorTransfer.DischargingInspectionToExit)
        {
            _transfer = ConveyorTransfer.DischargingInspectionFromExit;
        }

        if (input is InputIo.MainConveyorAvailableFromFront2
            or InputIo.MainConveyorReadyFromRear
            or InputIo.MainConveyorEntryCarrierDetected
            or InputIo.MainConveyorExitCarrierDetected)
        {
            Changed?.Invoke();
        }
    }

    private void OnCarrierChanged(StationPosition station, bool value)
    {
        if (!value)
        {
            return;
        }

        switch (station)
        {
            case StationPosition.BoltFastening when _transfer == ConveyorTransfer.PcbPlacementToBoltFastening:
                if (Interlocked.Exchange(ref _transferJob, null) is { } placementJob)
                    _placementWork.TransferAssembliesTo(_boltFasteningWork, placementJob);
                break;

            case StationPosition.Inspection when _transfer == ConveyorTransfer.BoltFasteningToInspection:
                if (Interlocked.Exchange(ref _transferJob, null) is { } fasteningJob)
                    _boltFasteningWork.TransferAssembliesTo(_inspectionWork, fasteningJob);
                break;
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private enum StationPosition
    {
        PcbPlacement,
        BoltFastening,
        Inspection,
    }

    private enum ConveyorTransfer
    {
        None,
        ReceivingBeforeEntry,
        ReceivingAfterEntry,
        PcbPlacementToBoltFastening,
        BoltFasteningToInspection,
        DischargingInspectionToExit,
        DischargingInspectionFromExit,
    }
}
