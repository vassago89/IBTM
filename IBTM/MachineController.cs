using System;
using System.Collections.Generic;
using System.ComponentModel;
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

public sealed partial class MachineController : INotifyPropertyChanged
{
    private static readonly InputIo[] s_carrierInputs;

    private readonly MachineState _state;
    private readonly MachineFeedbackMonitor _feedback;
    private readonly OperationCancellation _operations;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly RecipeManager _recipes;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly PcbSupplier _pcbSupply;
    private readonly PcbPlacer _pcbPlacement;
    private readonly BoltFasteningStation _fasteningStation;
    private readonly InspectionStation _inspectionStation;
    private readonly BoltFeederUnit _boltFeeder;
    private readonly ILogger<MachineController>? _log;

    static MachineController()
    {
        s_carrierInputs = [
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
        IIoService io,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        PcbSupplier pcbSupply,
        PcbPlacer pcbPlacement,
        BoltFasteningStation fasteningStation,
        InspectionStation inspectionStation,
        BoltFeederUnit boltFeeder,
        PcbHistory pcbHistory,
        ILogger<MachineController>? log = null)
    {
        _resetGate = new();

        _state = state;
        _feedback = feedback;
        _operations = operations;
        _options = options;
        _units = units;
        _recipes = recipes;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _fasteningStation = fasteningStation;
        _inspectionStation = inspectionStation;
        _boltFeeder = boltFeeder;
        PcbHistory = pcbHistory;
        _log = log;
        if (log is not null)
        {
            AutoUnit[] automaticUnits = [conveyor, pcbSupply, pcbPlacement, fasteningStation,
                inspectionStation, boltFeeder, ngConveyor];
            foreach (var unit in automaticUnits)
                unit.Trace += message => log.LogInformation("{Message}", message);
        }
        state.PropertyChanged += OnMachinePropertyChanged;
        recipes.Changed += OnRecipeChanged;
        conveyor.Changed += state.Refresh;
        ngConveyor.Changed += OnNgConveyorChanged;
        io.InputChanged += OnInputChanged;
        feedback.IoFaulted += OnIoFaulted;
        pcbPlacement.Feedback.MovingChanged += _ => CheckMotionInterlocks();
        fasteningStation.Feedback.StateChanged += CheckMotionInterlocks;
        inspectionStation.Feedback.MovingChanged += _ => CheckMotionInterlocks();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PcbHistory PcbHistory { get; }

    internal bool IsHomeAxisAllowed(MotionGroup group, MotionAxis axis)
    {
        return _state.Available && !_state.IsRunning && IsHomeAxisReady(group, axis, live: false);
    }

    private void OnRecipeChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(StartBlock)));
        PropertyChanged?.Invoke(this, new(nameof(IsStartAllowed)));
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

        UpdateMachineIndicators();
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
            _pcbSupply.StopMotion,
            _pcbPlacement.StopMotion,
            _fasteningStation.StopMotion,
            _inspectionStation.StopMotion,
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
        var cleanup = Task.WhenAll(shutdown, _inspectionStation.StopLiveViewAsync());
        try
        {
            await cleanup;
            UpdateMachineIndicators();
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

    private void OnMachinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MachineState.Ready)
            or nameof(MachineState.SafetyReady) or nameof(MachineState.DoorInterlockReady)
            or nameof(MachineState.Alarm) or nameof(MachineState.RepeatEnabled))
        {
            PropertyChanged?.Invoke(this, new(nameof(StartBlock)));
            PropertyChanged?.Invoke(this, new(nameof(IsStartAllowed)));
            PropertyChanged?.Invoke(this, new(nameof(IsHomeAllowed)));
            PropertyChanged?.Invoke(this, new(nameof(IsResetAllowed)));
        }
        if (e.PropertyName == nameof(MachineState.IsRunning))
        {
            PropertyChanged?.Invoke(this, new(nameof(IsStartAllowed)));
            PropertyChanged?.Invoke(this, new(nameof(IsHomeAllowed)));
            PropertyChanged?.Invoke(this, new(nameof(IsResetAllowed)));
        }
        if (e.PropertyName is null or nameof(MachineState.ManualSetupEnabled))
        {
            PropertyChanged?.Invoke(this, new(nameof(HomeBlock)));
            PropertyChanged?.Invoke(this, new(nameof(IsHomeAllowed)));
        }
        if (e.PropertyName is nameof(MachineState.Alarm) or nameof(MachineState.AutomaticRunning))
            UpdateMachineIndicators();
    }

    private void OnNgConveyorChanged()
    {
        UpdateMachineIndicators();
        _state.Refresh();
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

        if (Array.IndexOf(s_carrierInputs, input) >= 0
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

        if (input == InputIo.ResetButton && value && _options.UseResetButton)
        {
            _ = ResetAsync();
        }
    }

    private void CheckMotionInterlocks()
    {
        if (_units.BoltFastening
            && _fasteningStation.Feedback.Command == MotionCommand.Adjustment
            && (!IsManualMotionReady(MotionGroup.BoltFastening)
                || _state.AutomaticRunning
                || _state.IsHoming
                || _state.BoltTestRunning))
        {
            StopAndReportFailure();
        }

        var fasteningBlocked = _units.BoltFastening
            && _fasteningStation.Feedback.Command != MotionCommand.Adjustment
            && !_fasteningStation.IsHorizontalMoveAllowed
            && _fasteningStation.Feedback.IsMovingHorizontal;
        var alarm = MachineAlarm.None;
        string? interlockDetail = null;
        if (_units.PcbPlacement
            && !_pcbPlacement.HandlerRaised
            && _pcbPlacement.Feedback.IsMoving
            && (_pcbPlacement.Feedback.Command != MotionCommand.Adjustment
                || _pcbPlacement.Feedback.IsMovingHorizontal
                || !IsManualMotionReady(MotionGroup.PcbPlacementHandler)
                || _state.AutomaticRunning
                || _state.IsHoming))
        {
            alarm = MachineAlarm.PcbPlacement;
            interlockDetail = "PCB placement axis movement requires the handler lift Up. "
                + $"Current lift: {_pcbPlacement.Lift}.";
        }
        else if (fasteningBlocked)
        {
            alarm = MachineAlarm.BoltFastening;
            interlockDetail = "Fastening horizontal movement requires both heads Up. "
                + $"Current pickup head: {_fasteningStation.PickupHeadPosition}; "
                + $"shooting head: {_fasteningStation.ShootingHeadPosition}.";
        }
        else if (InspectionGantryEnabled
            && _inspectionStation.Feedback.IsMoving
            && !_inspectionStation.IsRaised)
        {
            alarm = MachineAlarm.NgCarrierTransfer;
            interlockDetail = "Inspection/NG horizontal movement requires the pickup Up. "
                + $"Current lift: {_inspectionStation.Lift}.";
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
            _boltFeeder.Stop,
            () => _fasteningStation.StopShooting(),
            () => _fasteningStation.StopIoStart(FasteningHead.Pickup),
            () => _fasteningStation.StopIoStart(FasteningHead.Shooting),
            _ngConveyor.Stop,
            _pcbSupply.StopUpstream,
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
