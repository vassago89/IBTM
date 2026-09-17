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
    // Arrival history for the selected transfer; Heat Sink 1 can pass the sensor during the push.
    private bool _heatSink1Arrived;
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
        _placement.CarrierChanged += OnPlacementCarrierChanged;
        _boltFastening.CarrierChanged += OnBoltFasteningCarrierChanged;
        _inspection.CarrierChanged += OnInspectionCarrierChanged;
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
            return DownstreamReady
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
                return DownstreamReady
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
            && !_placementWork.CarrierSeated)
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
        if (CarrierCount > 1 || ExitCarrierDetected)
            throw new InvalidOperationException("Main conveyor return requires one carrier and a clear exit.");

        // Reverse travel does not transfer production results to stations it passes.
        _transfer = ConveyorTransfer.None;
        _transferJob = null;
        _heatSink1Arrived = false;
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
            MainConveyorState.WaitingForFrontCarrier => "Front 2 Available=ON (teaching: TEST, auto: DI)",
            MainConveyorState.WaitingForRearEquipment => "Rear Ready=ON (teaching: TEST, auto: DI)",
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
                    _boltFasteningWork,
                    _inspectionWork,
                    cancellationToken);
                break;
            case MainConveyorState.MovingPcbPlacementToBoltFastening:
                await MoveCarrierAsync(
                    ConveyorTransfer.PcbPlacementToBoltFastening,
                    _placementWork,
                    _boltFasteningWork,
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
        _io.SetAutomaticSmemaOutput(
            OutputIo.MainConveyorReadyToFront2,
            !_repeat && _placementWork.CanReceive && !rearAvailable);
        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, rearAvailable);
    }

    private async Task ReceiveAtPlacementAsync(CancellationToken cancellationToken)
    {
        if (_transfer is not ConveyorTransfer.ReceivingBeforeEntry
            and not ConveyorTransfer.ReceivingAfterEntry)
        {
            _transfer = EntryCarrierDetected
                ? ConveyorTransfer.ReceivingAfterEntry
                : ConveyorTransfer.ReceivingBeforeEntry;
        }

        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, false);
        Exception? failure = null;
        try
        {
            if (!_placement.CarrierPresent)
                await _placement.PrepareToReceiveAsync(cancellationToken);
            else
                _transfer = ConveyorTransfer.ReceivingAfterEntry;

            RequireSeatingPushPosition(_placement);
            if (!_repeat && _transfer == ConveyorTransfer.ReceivingBeforeEntry)
                _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, true);

            StartMotor(cancellationToken);
            if (_transfer == ConveyorTransfer.ReceivingBeforeEntry)
            {
                await _io.WaitForInputAsync(
                    InputIo.MainConveyorEntryCarrierDetected,
                    true,
                    cancellationToken);
                _transfer = ConveyorTransfer.ReceivingAfterEntry;
            }

            _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            await CompleteSeatingPushAsync(_placementWork, cancellationToken);
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
        _heatSink1Arrived = false;
        _transfer = ConveyorTransfer.None;
    }

    private async Task MoveCarrierAsync(
        ConveyorTransfer transfer,
        StationWork sourceWork,
        StationWork destinationWork,
        CancellationToken cancellationToken)
    {
        var source = sourceWork.Station;
        var destination = destinationWork.Station;
        if (_transfer != transfer)
            _transferJob = sourceWork.CurrentJob;
        _transfer = transfer;
        TraceStep(transfer, workId: _transferJob?.Id, waitingFor:
            transfer == ConveyorTransfer.PcbPlacementToBoltFastening
                ? "BoltFasteningHeatSink1Present=ON"
                : "InspectionHeatSink1Present=ON");
        StopOutputs(null, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
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
            await CompleteSeatingPushAsync(destinationWork, cancellationToken);
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

        if (!destination.CarrierPresent)
            throw new InvalidOperationException("Carrier presence was lost before raising the backup plate.");
        // Travel and the timed push are finished. STOP during ascent resumes from
        // the normal seating state, without replaying the transfer.
        _heatSink1Arrived = false;
        _transfer = ConveyorTransfer.None;
        await destination.SeatAsync(cancellationToken);
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
        StationWork destinationWork,
        CancellationToken cancellationToken)
    {
        var destination = destinationWork.Station;
        if (!_heatSink1Arrived)
        {
            await destination.WaitForHeatSink1Async(cancellationToken, Timeout.Infinite);
            _heatSink1Arrived = true;
        }
        var carrierLeft = new AsyncAutoResetEvent();
        destination.CarrierChanged += StopWhenCarrierLeaves;
        try
        {
            if (!destination.CarrierPresent)
                carrierLeft.Set();
            TraceStep(_transfer, target: "seating push", workId: destinationWork.CurrentJob.Id, waitingFor:
                $"Heat Sink 1 detected; push for {_settings.CarrierStopDelaySeconds} s");
            // Heat Sink 1 starts the push duration. STOP leaves it unfinished; resume
            // requires the full uninterrupted duration before raising the plate.
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
            destination.CarrierChanged -= StopWhenCarrierLeaves;
        }

        void StopWhenCarrierLeaves(bool present)
        {
            if (!present)
                carrierLeft.Set();
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

        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetAutomaticSmemaOutput(
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
        InputIo? arrival = _transfer switch
        {
            ConveyorTransfer.ReceivingBeforeEntry or ConveyorTransfer.ReceivingAfterEntry => InputIo.PcbPlacementHeatSink1Present,
            ConveyorTransfer.PcbPlacementToBoltFastening => InputIo.BoltFasteningHeatSink1Present,
            ConveyorTransfer.BoltFasteningToInspection => InputIo.InspectionHeatSink1Present,
            _ => null,
        };
        if (input == arrival && value)
            _heatSink1Arrived = true;

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

    private void OnPlacementCarrierChanged(bool present)
    {
        if (!present
            && _transfer is ConveyorTransfer.ReceivingBeforeEntry or ConveyorTransfer.ReceivingAfterEntry)
            _heatSink1Arrived = false;
    }

    private void OnBoltFasteningCarrierChanged(bool present)
    {
        if (!present && _transfer == ConveyorTransfer.PcbPlacementToBoltFastening)
            _heatSink1Arrived = false;
        if (present
            && _transfer == ConveyorTransfer.PcbPlacementToBoltFastening
            && Interlocked.Exchange(ref _transferJob, null) is { } job)
        {
            _placementWork.TransferAssembliesTo(_boltFasteningWork, job);
        }
    }

    private void OnInspectionCarrierChanged(bool present)
    {
        if (!present && _transfer == ConveyorTransfer.BoltFasteningToInspection)
            _heatSink1Arrived = false;
        if (present
            && _transfer == ConveyorTransfer.BoltFasteningToInspection
            && Interlocked.Exchange(ref _transferJob, null) is { } job)
        {
            _boltFasteningWork.TransferAssembliesTo(_inspectionWork, job);
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
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
