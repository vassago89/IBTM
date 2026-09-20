using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM;

public sealed partial class MachineController
{
    private static readonly InputIo[] CarrierInputs;

    private readonly MachineState _state;
    private readonly MachineFeedbackMonitor _feedback;
    private readonly OperationCancellation _operations;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly RecipeManager _recipes;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly NgShuttle _ngShuttle;
    private readonly PcbSupplier _pcbSupply;
    private readonly PcbPlacer _pcbPlacement;
    private readonly BoltFasteningStation _fasteningStation;
    private readonly InspectionStation _inspectionStation;
    private readonly PickupBoltFeeder _pickupBoltFeeder;
    private readonly ShootingBoltFeeder _shootingBoltFeeder;
    private readonly PcbSupplyHandler _supplyHandler;
    private readonly PcbPlacementHandler _placementHandler;
    private readonly BoltFasteningGantry _fasteningGantry;
    private readonly InspectionGantry _inspectionGantry;
    private readonly NgCarrierTransfer _ngTransfer;
    private readonly NgCarrierMove _ngMove;
    private readonly BoltInspector _boltInspector;
    private readonly ILogger<MachineController>? _log;

    static MachineController()
    {
        CarrierInputs = [
            InputIo.MainConveyorEntryCarrierDetected,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementHeatSink2Present,
            InputIo.BoltFasteningHeatSink1Present,
            InputIo.BoltFasteningHeatSink2Present,
            InputIo.InspectionHeatSink1Present,
            InputIo.InspectionHeatSink2Present,
            InputIo.MainConveyorExitCarrierDetected,
            InputIo.NgCarrierDetected,
            InputIo.NgShuttleCarrierDetected,
            InputIo.NgConveyorPosition1Occupied,
            InputIo.NgConveyorPosition2Occupied,
        ];
    }

    public MachineController(
        MachineState state,
        MachineFeedbackMonitor feedback,
        OperationCancellation operations,
        MachineOptions options,
        UnitSettings units,
        RecipeManager recipes,
        CarrierReferenceSettings carrierReference,
        IIoService io,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        NgShuttle ngShuttle,
        PcbSupplier pcbSupply,
        PcbPlacer pcbPlacement,
        BoltFasteningStation fasteningStation,
        InspectionStation inspectionStation,
        PickupBoltFeeder pickupBoltFeeder,
        ShootingBoltFeeder shootingBoltFeeder,
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        BoltFasteningGantry fasteningGantry,
        InspectionGantry inspectionGantry,
        NgCarrierTransfer ngTransfer,
        NgCarrierMove ngMove,
        BoltInspector boltInspector,
        ILogger<MachineController>? log = null)
    {
        _resetGate = new();

        _state = state;
        _feedback = feedback;
        _operations = operations;
        _options = options;
        _units = units;
        _recipes = recipes;
        _carrierReference = carrierReference;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _ngShuttle = ngShuttle;
        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _fasteningStation = fasteningStation;
        _inspectionStation = inspectionStation;
        _pickupBoltFeeder = pickupBoltFeeder;
        _shootingBoltFeeder = shootingBoltFeeder;
        _supplyHandler = supplyHandler;
        _placementHandler = placementHandler;
        _fasteningGantry = fasteningGantry;
        _inspectionGantry = inspectionGantry;
        _ngTransfer = ngTransfer;
        _ngMove = ngMove;
        _boltInspector = boltInspector;
        _log = log;
        if (log is not null)
        {
            AutoUnit[] automaticUnits = [conveyor, pcbSupply, pcbPlacement, fasteningStation,
                inspectionStation, pickupBoltFeeder, shootingBoltFeeder, ngMove, ngConveyor, ngShuttle];
            foreach (var unit in automaticUnits)
                unit.Trace += message => log.LogInformation("{Message}", message);
        }
        io.InputChanged += OnInputChanged;
        feedback.IoFaulted += OnIoFaulted;
        placementHandler.Feedback.MovingChanged += _ => CheckMotionInterlocks();
        fasteningGantry.Feedback.StateChanged += CheckMotionInterlocks;
        inspectionGantry.Feedback.MovingChanged += _ => CheckMotionInterlocks();
        pcbSupply.Changed += state.RequestDisplayRefresh;
        pcbPlacement.Changed += state.RequestDisplayRefresh;
        fasteningStation.Changed += state.RequestDisplayRefresh;
        fasteningGantry.Changed += state.RequestDisplayRefresh;
        inspectionStation.Changed += state.RequestDisplayRefresh;
        pickupBoltFeeder.Changed += state.RequestDisplayRefresh;
        shootingBoltFeeder.Changed += state.RequestDisplayRefresh;
    }

    private bool PcbHandlersEnabled => _units.PcbSupply || _units.PcbPlacement;

    private bool InspectionGantryEnabled => _units.IsMotionEnabled(MotionGroup.InspectionGantry);

    public async Task InitializeAsync()
    {
        _log?.LogInformation("Machine initialization started.");
        using (var operation = _operations.TryBegin())
        {
            if (operation is null)
                return;
            var (alarm, error) = await InitializeHardwareAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (alarm == MachineAlarm.None)
                alarm = SafetyAlarm;

            if (alarm == MachineAlarm.None)
                _state.Refresh();
            else
                _state.SetError(alarm, error);
        }

        _state.UpdateMachineIndicators();
        await _state.StartDisplayUpdatesAsync(ReadDisplay);
        _log?.LogInformation("{Message}", $"Machine initialization finished. Alarm={_state.Alarm}.");
    }

    public async Task StopAsync()
    {
        await Task.Run(Stop);
    }

    public void Stop()
    {
        _log?.LogInformation("Machine STOP requested.");
        Exception? failure = null;
        try
        {
            _operations.Cancel();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Action[] stops = [
            StopRunOutputs,
            _supplyHandler.StopMotion,
            _placementHandler.StopMotion,
            _fasteningGantry.StopMotion,
            _inspectionGantry.StopMotion,
        ];
        foreach (var stop in stops)
        {
            try
            {
                stop();
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
        }

        try
        {
            _state.Refresh();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    public async Task ShutdownAsync()
    {
        // Cancellation callbacks and final feedback reads call synchronous device SDKs.
        await Task.Run(ShutdownHardwareAsync);
    }

    private async Task ShutdownHardwareAsync()
    {
        _log?.LogInformation("Machine shutdown requested.");
        var displayStopped = _state.StopDisplayUpdatesAsync();
        var shutdown = _operations.ShutdownAsync();
        Exception? failure = null;
        try
        {
            StopRunOutputs();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // Cleanup may still await cylinder inputs. Keep every monitor alive
        // until command/device cleanup finishes, then stop and join the loops.
        var cleanup = Task.WhenAll(shutdown, displayStopped, _boltInspector.StopLiveViewAsync());
        try
        {
            await cleanup;
            _state.UpdateMachineIndicators();
        }
        catch (Exception exception)
        {
            var cleanupFailure = cleanup.Exception is { InnerExceptions.Count: > 1 } errors
                ? errors
                : exception;
            failure = failure is null ? cleanupFailure : new AggregateException(failure, cleanupFailure);
        }

        // Command cleanup is not proof that axes moved outside this application have stopped.
        // Check current feedback once, including disabled groups, before completing shutdown.
        foreach (var (group, motion) in _feedback.Motions)
        {
            foreach (var axis in motion.Feedback.Axes)
            {
                Exception? stopFailure = null;
                try
                {
                    if (motion.Feedback.GetAxisState(axis).InMotion)
                        stopFailure = new InvalidOperationException("The axis still reports motion.");
                }
                catch (Exception exception)
                {
                    stopFailure = exception;
                }

                if (stopFailure is not null)
                {
                    stopFailure = new MotionException($"Confirm {group} {axis} stopped", stopFailure);
                    failure = failure is null ? stopFailure : new AggregateException(failure, stopFailure);
                }
            }
        }

        try
        {
            await _feedback.StopAsync();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (MachineState.IsSafetyInput(input))
        {
            var alarm = SafetyAlarm;
            if (alarm != MachineAlarm.None)
            {
                StopAndReportFailure(alarm);
                return;
            }
        }

        if (input == InputIo.AutoMode
            && (value || _state.AutoMode && !_state.AutomaticRunning))
            StopAndReportFailure();

        if (input is InputIo.AutoMode
            or InputIo.PcbPlacementHandlerUp
            or InputIo.PcbPlacementHandlerDown
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadDown
            or InputIo.NgCarrierPickupUp
            or InputIo.NgCarrierPickupDown)
        {
            CheckMotionInterlocks();
        }

        if (Array.IndexOf(CarrierInputs, input) >= 0
            || input is InputIo.PcbPlacementPcbDetected
                or InputIo.PcbPlacementHandlerUp
                or InputIo.PcbPlacementHandlerDown
                or InputIo.PcbPlacementIpmUp
                or InputIo.PcbPlacementIpmDown
                or InputIo.PickupHeadUp
                or InputIo.PickupHeadDown
                or InputIo.ShootingHeadUp
                or InputIo.ShootingHeadDown
                or InputIo.NgCarrierPickupUp
                or InputIo.NgCarrierPickupDown)
            _state.Refresh();

        if (input == InputIo.ServoMainContactorOn || MachineState.IsSafetyInput(input))
            _state.RequestDisplayRefresh();

        if (input == InputIo.ResetButton && value && _options.UseResetButton)
        {
            _ = ResetAsync();
        }
    }

    private void CheckMotionInterlocks()
    {
        if (_units.BoltFastening
            && _fasteningGantry.Feedback.Command == MotionCommand.Adjustment
            && (!IsManualMotionReady(MotionGroup.BoltFastening)
                || _state.AutomaticRunning
                || _state.IsHoming
                || _state.BoltTestRunning))
        {
            StopAndReportFailure();
        }

        var fasteningBlocked = _units.BoltFastening
            && _fasteningGantry.Feedback.Command != MotionCommand.Adjustment
            && !_fasteningGantry.IsHorizontalMoveAllowed
            && _fasteningGantry.Feedback.IsMovingHorizontal;
        var alarm = MachineAlarm.None;
        string? interlockDetail = null;
        if (_units.PcbPlacement
            && !_placementHandler.HandlerRaised
            && _placementHandler.Feedback.IsMoving)
        {
            alarm = MachineAlarm.PcbPlacement;
            interlockDetail = "PCB placement axis movement requires the handler lift Up. "
                + $"Current lift: {_placementHandler.Lift}.";
        }
        else if (fasteningBlocked)
        {
            alarm = MachineAlarm.BoltFastening;
            interlockDetail = "Fastening horizontal movement requires both heads Up. "
                + $"Current pickup head: {_fasteningGantry.PickupHeadPosition}; "
                + $"shooting head: {_fasteningGantry.ShootingHeadPosition}.";
        }
        else if (InspectionGantryEnabled
            && _inspectionGantry.Feedback.IsMoving
            && !_ngTransfer.IsRaised)
        {
            alarm = MachineAlarm.NgCarrierTransfer;
            interlockDetail = "Inspection/NG horizontal movement requires the pickup Up. "
                + $"Current lift: {_ngTransfer.Lift}.";
        }

        if (alarm != MachineAlarm.None)
        {
            StopAndReportFailure(
                _state.IsError ? _state.Alarm : alarm,
                new MotionInterlockException(interlockDetail!));
        }
    }

    private void StopAndReportFailure(MachineAlarm alarm = MachineAlarm.None, Exception? cause = null)
    {
        Exception? failure = null;
        try
        {
            if (alarm != MachineAlarm.None)
                _state.SetError(alarm, cause);
        }
        catch (Exception exception)
        {
            // An alarm subscriber can fail while cancelling a native motion.
            // Still attempt STOP on the remaining devices.
            failure = exception;
        }

        try
        {
            Stop();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is null)
            return;
        if (failure is not IOException and not MotionException
            && (failure is not AggregateException aggregate
                || aggregate.Flatten().InnerExceptions.Any(error => error is not IOException and not MotionException)))
            ExceptionDispatchInfo.Throw(failure);

        // A failed actuator STOP must not terminate the input/motion feedback loop.
        // Preserve the trip alarm; attach or log the additional device failure.
        _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.StopFailed, failure);
    }

    private MachineAlarm SafetyAlarm
    {
        get
        {
            switch (true)
            {
                case true when _options.UseEmergencyStop && !_state.EmergencyStopReleased:
                    return MachineAlarm.EmergencyStop;
                case true when !_state.DoorInterlockReady:
                    return MachineAlarm.DoorOpen;
                default:
                    return _options.UseAirPressureInterlock && !_state.AirPressureOk
                        ? MachineAlarm.AirPressureLow
                        : MachineAlarm.None;
            }
        }
    }

    internal void StopRunOutputs()
    {
        // One device's failed STOP must not skip STOP on the remaining devices.
        Action[] stops = [
            _conveyor.Stop,
            _shootingBoltFeeder.Stop,
            () => _fasteningGantry.StopShooting(),
            () => _fasteningGantry.StopIoStart(FasteningHead.Pickup),
            () => _fasteningGantry.StopIoStart(FasteningHead.Shooting),
            _ngConveyor.Stop,
            _supplyHandler.StopUpstream,
        ];
        List<Exception>? failures = null;
        foreach (var stop in stops)
        {
            try
            {
                stop();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more devices could not be stopped.", failures);
    }

    private static bool IsMotionFailure(Exception exception)
    {
        return exception is MotionException
            || exception is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.Any(error => error is MotionException);
    }

    private void OnIoFaulted(Exception exception)
    {
        StopAndReportFailure(MachineAlarm.IoCommunication, exception);
    }
}
