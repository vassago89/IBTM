using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
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
    private readonly IReadOnlyDictionary<MotionGroup, IXyMotion> _motions;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly PcbSupplier _pcbSupply;
    private readonly PcbPlacer _pcbPlacement;
    private readonly BoltFasteningStation _fasteningStation;
    private readonly InspectionStation _inspectionStation;
    private readonly BoltFeederUnit _boltFeeder;
    private readonly ILogger<MachineController>? _log;
    private readonly Lock _resetGate;
    // Last handled notification, not the physical lamp/buzzer state.
    private (MachineAlarm Alarm, bool Running, bool NgAlarm)? _lastIndicatorNotification;

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
        IReadOnlyDictionary<MotionGroup, IXyMotion> motions,
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
        _motions = motions;
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
        pcbPlacement.Motion.Feedback.MovingChanged += _ => CheckMotionInterlocks();
        fasteningStation.Motion.Feedback.StateChanged += CheckMotionInterlocks;
        inspectionStation.Motion.Feedback.MovingChanged += _ => CheckMotionInterlocks();
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
            _motions[MotionGroup.PcbSupply].Stop,
            _motions[MotionGroup.PcbPlacementHandler].Stop,
            _motions[MotionGroup.BoltFastening].Stop,
            _motions[MotionGroup.InspectionGantry].Stop,
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
            && _fasteningStation.Motion.Feedback.Command == MotionCommand.Adjustment
            && (!IsManualMotionReady(MotionGroup.BoltFastening)
                || _state.AutomaticRunning
                || _state.IsHoming
                || _state.BoltTestRunning))
        {
            StopAndReportFailure();
        }

        var fasteningBlocked = _units.BoltFastening
            && _fasteningStation.Motion.Feedback.Command != MotionCommand.Adjustment
            && !_fasteningStation.IsHorizontalMoveAllowed
            && _fasteningStation.Motion.Feedback.IsMovingHorizontal;
        var alarm = MachineAlarm.None;
        string? interlockDetail = null;
        if (_units.PcbPlacement
            && !_pcbPlacement.HandlerRaised
            && _pcbPlacement.Motion.Feedback.IsMoving
            && (_pcbPlacement.Motion.Feedback.Command != MotionCommand.Adjustment
                || _pcbPlacement.Motion.Feedback.IsMovingHorizontal
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
            && _inspectionStation.Motion.Feedback.IsMoving
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

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeIoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = "Control I/O initialization";
        _log?.LogInformation("{Message}", stage + " started.");
        try
        {
            // Keep SDK initialization off the input notification thread.
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _io.Initialize();
                    cancellationToken.ThrowIfCancellationRequested();
                    stage = "Control I/O readiness check";
                    _io.CheckReady();
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stage = "Stopping run outputs after I/O initialization";
            StopRunOutputs();
            stage = "Setting conveyor normal speed outputs";
            _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
            _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
            stage = "Setting placement handler rotation OFF";
            _io.SetOutput(OutputIo.PcbPlacementHandlerRotate, false);
            stage = "Setting main conveyor forward direction";
            _io.SetOutput(OutputIo.MainConveyorForward, true);
            _log?.LogInformation("Control I/O initialization and readiness check completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.LogError("{Message}", $"{stage} failed. {exception.Message}");
            return (MachineAlarm.IoCommunication, exception);
        }

        return (MachineAlarm.None, null);
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeHardwareAsync(
        CancellationToken cancellationToken)
    {
        var ioResult = await InitializeIoAsync(cancellationToken);
        // Input feedback must keep updating during the remaining device initialization.
        await _feedback.StartAsync();
        if (ioResult.Alarm != MachineAlarm.None)
        {
            return ioResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stage = "Motion initialization";
        try
        {
            if (_units.PcbSupply)
            {
                stage = "PCB supply motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.PcbSupply].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.PcbPlacement)
            {
                stage = "PCB placement motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.PcbPlacementHandler].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.BoltFastening)
            {
                stage = "Bolt fastening motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.BoltFastening].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (InspectionGantryEnabled)
            {
                stage = "Inspection motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.InspectionGantry].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _log?.LogInformation("Motion initialization completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.LogError("{Message}", $"{stage} failed. {exception.Message}");
            return (MachineAlarm.MotionUnavailable, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                _log?.LogInformation("Vision / lighting initialization started.");
                await _inspectionStation.InitializeVisionAsync(cancellationToken);
                _log?.LogInformation("Vision / lighting initialization completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.LogError("{Message}", $"Vision / lighting initialization failed. {exception.Message}");
                return (MachineAlarm.Inspection, exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                _log?.LogInformation("Bolt controller readiness check started.");
                await _fasteningStation.CheckReadyAsync(cancellationToken);
                _log?.LogInformation("Bolt controller readiness check completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.LogError("{Message}", $"Bolt controller readiness check failed. {exception.Message}");
                return (MachineAlarm.BoltFastening, exception);
            }
        }

        return (MachineAlarm.None, null);
    }

    public bool IsStartAllowed => _state.Available && IsStartAllowedFor(StartBlock, _state.IsRunning);

    public StartBlockReason StartBlock => GetStartBlock(_state.FeedbackReadiness);

    public bool TeachingReady
    {
        get
        {
            switch (true)
            {
                case true when (_units.BoltFastening || _units.Inspection)
                    && _recipes.Current.Pcb.BoltPoints.Count == 0:
                    return false;
                case true when _units.BoltFastening
                    && _recipes.Current.Pcb.BoltPoints.Any(bolt => !bolt.IsFasteningPositionDefined):
                    return false;
                default:
                    return !_units.Inspection
                        || _recipes.Current.Pcb.BoltPoints.All(_inspectionStation.HasRegion)
                            && Enum.GetValues<HeatSinkSlot>().All(_inspectionStation.HasBarcodeRegion);
            }
        }
    }

    private bool IsStartAllowedFor(StartBlockReason block, bool? running = null)
    {
        return !_operations.IsShuttingDown
            && !(running ?? _state.IsRunningFor())
            && block == StartBlockReason.None;
    }

    private StartBlockReason GetStartBlock(MotionReadiness motion)
    {
        switch (true)
        {
            case true when _state.Alarm == MachineAlarm.EmergencyStop:
                return StartBlockReason.EmergencyStop;
            case true when _state.Alarm == MachineAlarm.DoorOpen:
                return StartBlockReason.DoorOpen;
            case true when _state.Alarm == MachineAlarm.AirPressureLow:
                return StartBlockReason.AirPressure;
            case true when _state.IsError:
                return StartBlockReason.Alarm;
            case true when _options.UseEmergencyStop && !_state.EmergencyStopReleased:
                return StartBlockReason.EmergencyStop;
            case true when _options.UseAirPressureInterlock && !_state.AirPressureOk:
                return StartBlockReason.AirPressure;
            case true when motion.Faulted:
                return StartBlockReason.MotionFault;
            case true when !motion.ServosOn || !_state.ServoMainContactorOn:
                return StartBlockReason.ServoOff;
            case true when !_state.DoorInterlockReady:
                return StartBlockReason.DoorOpen;
            case true when !motion.Homed:
                return StartBlockReason.HomeRequired;
            case true when _state.RepeatEnabled && !_state.ManualMode:
                return StartBlockReason.TeachingMode;
            case true when !TeachingReady:
                return StartBlockReason.TeachingIncomplete;
            default:
                return _units.IsAnyUnitEnabled ? StartBlockReason.None : StartBlockReason.NoUnitEnabled;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => RunAutomaticAsync(cancellationToken), cancellationToken);
    }

    private async Task RunAutomaticAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsStartAllowedFor(GetStartBlock(_state.MotionReadiness)))
                return;
        }
        catch (Exception exception) when (exception is IOException or MotionException)
        {
            StopAndReportFailure(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            return;
        }

        var repeat = _state.RepeatEnabled;
        var startedInManual = _state.ManualMode;
        var feedbackStartedAt = Stopwatch.GetTimestamp();
        using var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            return;
        void StopWhenOperationBecomesUnavailable()
        {
            switch (true)
            {
                case true when operation.IsCancellationRequested:
                    return;
                case true when _state.IsError:
                    operation.Cancel();
                    return;
            }

            try
            {
                var modeReady = _state.ManualMode == startedInManual;
                // An unsafe DI already requires a stop. Do not wait for unrelated
                // axis diagnostics before requesting it; safe operation still uses live SDK feedback.
                if (!_io.IsReady
                    || !modeReady
                    || !_state.SafetyReady
                    || !_state.DoorInterlockReady)
                {
                    _log?.LogInformation("Automatic stop: I/O, selector, emergency stop, air or door condition changed.");
                    operation.Cancel();
                    return;
                }

                if (!_state.ServoMainContactorOn)
                {
                    _state.SetError(MachineAlarm.MotionUnavailable);
                }
                else
                {
                    var motion = _state.MotionReadiness;
                    if (!motion.Homed || !motion.ServosOn || motion.Faulted)
                        _state.SetError(MachineAlarm.MotionUnavailable);
                    else
                        return;
                }
            }
            catch (Exception exception)
            {
                _state.SetError(MachineAlarm.MotionUnavailable, exception);
            }

            operation.Cancel();
        }

        void StopWhenMotionFeedbackBecomesUnavailable(MotionGroup group, MotionFeedbackSample sample)
        {
            // Live admission already checked the equipment. Do not apply a scan that
            // began before this run (for example, while Home was still completing).
            switch (true)
            {
                case true when operation.IsCancellationRequested
                    || sample.StartedAt < feedbackStartedAt
                    || !sample.Enabled
                    || !_units.IsMotionEnabled(group):
                    return;
                case true when _state.IsError:
                    operation.Cancel();
                    return;
            }

            var motion = sample.Readiness;
            if (sample.IoReady
                && sample.ReadError is null
                && motion.Homed
                && motion.ServosOn
                && !motion.Faulted)
                return;
            // Consume the completed scan, not a second native read that could miss a
            // transient fault. Disabled axes have already been excluded from this snapshot.
            try
            {
                _state.SetError(
                    sample.IoReady ? MachineAlarm.MotionUnavailable : MachineAlarm.IoCommunication,
                    sample.ReadError ?? new InvalidOperationException(
                        $"Motion feedback {group} became unavailable during automatic operation: "
                            + $"homed={motion.Homed}, servosOn={motion.ServosOn}, faulted={motion.Faulted}."));
            }
            finally
            {
                operation.Cancel();
            }
        }

        try
        {
            var (startAlarm, startError) = await InitializeHardwareAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (startAlarm != MachineAlarm.None)
            {
                _state.SetError(startAlarm, startError);
                return;
            }

            _state.Changed += StopWhenOperationBecomesUnavailable;
            _feedback.Sampled += StopWhenMotionFeedbackBecomesUnavailable;
            StopWhenOperationBecomesUnavailable();
            if (operation.IsCancellationRequested)
            {
                return;
            }

            await RaiseCylindersAsync(operation);

            if (_units.MainConveyor)
            {
                try
                {
                    await _conveyor.PrepareEmptyStationsAsync(operation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (!_state.IsError)
                        _state.SetError(MachineAlarm.MainConveyor, exception);
                    else
                        _log?.LogError(exception, "Main conveyor startup plate lowering failed while stopping.");
                    return;
                }
            }

            operation.Token.ThrowIfCancellationRequested();
            _state.AutomaticRunning = true;
            if (repeat)
            {
                await RunRepeatAsync(operation.Token);
            }
            else
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                await RunAutomaticUnitsAsync(cycle, repeat: false);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, exception);
        }
        finally
        {
            _state.Changed -= StopWhenOperationBecomesUnavailable;
            _feedback.Sampled -= StopWhenMotionFeedbackBecomesUnavailable;
            _state.AutomaticRunning = false;
            StopAndReportFailure();
        }
    }

    private async Task RunAutomaticUnitsAsync(CancellationTokenSource cycle, bool repeat)
    {
        var runningUnits = new List<Task>();
        if (!cycle.IsCancellationRequested && _units.PcbSupply && (!repeat || _units.PcbPlacement))
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.PcbSupply,
                _pcbSupply.RunAsync(_recipes.Current.PcbSupply, _pcbPlacement, cycle.Token, repeat),
                cycle));
        }

        if (!cycle.IsCancellationRequested)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.PcbPlacement,
                _pcbPlacement.RunAsync(cycle.Token, repeat),
                cycle));
        }

        if (!cycle.IsCancellationRequested && !repeat
            && (_units.PickupBoltFeeder || _units.ShootingBoltFeeder))
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                _units.ShootingBoltFeeder ? MachineAlarm.ShootingBoltFeeder : MachineAlarm.PickupBoltFeeder,
                _boltFeeder.RunAsync(cycle.Token),
                cycle));
        }

        if (!cycle.IsCancellationRequested)
        {
            if (_units.BoltFastening && (repeat || !_units.PickupBoltFeeder))
                _log?.LogInformation(
                    "Pickup bolt feeding is disabled for this run; pickup motion remains active without vacuum ON or bolt detection waits. The motor runs for the configured dry-run duration, then stops without waiting for a fastening result.");
            if (_units.BoltFastening && (repeat || !_units.ShootingBoltFeeder))
                _log?.LogInformation(
                    "Shooting bolt feeding is disabled for this run; bolt supply and shooting are skipped. The motor runs for the configured dry-run duration, then stops without waiting for a fastening result.");
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.BoltFastening,
                _fasteningStation.RunAsync(cycle.Token, repeat),
                cycle));
        }

        if (!cycle.IsCancellationRequested)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.Inspection,
                _inspectionStation.RunAsync(
                    cycle.Token,
                    repeat),
                cycle));
        }

        if (!cycle.IsCancellationRequested && _units.NgConveyor && !repeat)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.NgConveyor,
                _ngConveyor.RunAsync(cycle.Token, repeat),
                cycle));
        }

        // Stations report existing carrier work before the conveyor selects its first transfer.
        if (!cycle.IsCancellationRequested && _units.MainConveyor)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.MainConveyor,
                _conveyor.RunAsync(cycle.Token, repeat),
                cycle));
        }

        // STOP and repeat reversal wait for every unit's command cleanup.
        await Task.WhenAll(runningUnits).ConfigureAwait(false);
    }

    private async Task ObserveAutomaticUnitAsync(
        MachineAlarm alarm,
        Task running,
        CancellationTokenSource cycle)
    {
        try
        {
            await running;
            if (!cycle.IsCancellationRequested && !_state.IsError)
            {
                _state.SetError(alarm, new InvalidOperationException(
                    $"Automatic unit {alarm} returned before a stop was requested."));
            }
        }
        catch (OperationCanceledException) when (cycle.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (alarm is MachineAlarm.PickupBoltFeeder or MachineAlarm.ShootingBoltFeeder
                && exception is IoTimeoutException timeout)
                alarm = timeout.Input == InputIo.PickupFeederBoltDetected
                    ? MachineAlarm.PickupBoltFeeder : MachineAlarm.ShootingBoltFeeder;
            if (!_state.IsError)
            {
                // Keep the unit name even when the alarm is classified as MotionUnavailable.
                // SetError records the original exception and its full stack trace.
                _log?.LogError("{Message}", $"Automatic unit {alarm} failed. {exception.Message}");
                _state.SetError(
                    IsMotionFailure(exception) ? MachineAlarm.MotionUnavailable : alarm,
                    exception);
            }
            else
            {
                _log?.LogError(exception, "{Message}", $"Automatic unit {alarm} failed while stopping; existing alarm={_state.Alarm}.");
            }
        }
        finally
        {
            cycle.Cancel();
        }
    }

    public bool IsHomeAllowed => _state.Available && IsHomeAllowedFor(_state.FeedbackReadiness, _state.IsRunning);

    public HomeBlockReason HomeBlock => GetHomeBlock();

    private bool IsHomeAllowedFor(MotionReadiness motion, bool? running = null)
    {
        return !_operations.IsShuttingDown
            && _state.SafetyReady
            && motion.ServosOn
            && !motion.Faulted
            && _state.ServoMainContactorOn
            && !_state.IsError
            && !(running ?? _state.IsRunningFor())
            && HomeBlock == HomeBlockReason.None;
    }

    internal HomeBlockReason GetHomeBlock(MotionGroup? group = null, bool requireRaised = false)
    {
        switch (true)
        {
            case true when !_io.IsReady:
                return HomeBlockReason.IoUnavailable;
            case true when group is { } motionGroup && !_units.IsMotionEnabled(motionGroup):
                return HomeBlockReason.UnitDisabled;
            case true when !_state.ManualMode && !_state.DoorInterlockReady:
                return HomeBlockReason.DoorOpen;
            case true when (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
                || group is null
                && PcbHandlersEnabled)
                && _io.GetInput(InputIo.PcbPlacementPcbDetected)
                && _pcbPlacement.IpmLift != PlacementCylinderState.Up:
                return HomeBlockReason.PlacementHoldingPcb;
            case true when requireRaised
                && (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
                    || group is null && PcbHandlersEnabled)
                && (!_pcbPlacement.HandlerRaised
                    || _pcbPlacement.IpmLift != PlacementCylinderState.Up):
                return HomeBlockReason.PlacementNotRaised;
            case true when requireRaised
                && (group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
                && !_fasteningStation.IsHorizontalMoveAllowed:
                return HomeBlockReason.FasteningNotRaised;
            case true when requireRaised && (group == MotionGroup.InspectionGantry
                || group is null
                && InspectionGantryEnabled)
                && !_inspectionStation.IsRaised:
                return HomeBlockReason.NgPickupNotRaised;
            default:
                return HomeBlockReason.None;
        }
    }

    private bool IsHomeAxisReady(
        MotionGroup group, MotionAxis? axis = null, bool live = true, bool requireRaised = false)
    {
        if (!_state.ManualMode
            || !_state.SafetyReady
            || !_state.ServoMainContactorOn
            || GetHomeBlock(group, requireRaised) != HomeBlockReason.None)
            return false;

        var motion = _state.GetMotionStatus(group);
        return motion.IsReady(live)
            && motion.Feedback.Axes.Where(candidate => axis is null || candidate == axis)
                .All(
                    candidate =>
                        (live ? motion.Feedback.GetAxisState(candidate) : motion.Axes[candidate].State)
                            is { ServoOn: true, Alarm: false, Emergency: false });
    }

    internal async Task HomeAsync(
        MotionGroup group,
        CancellationToken cancellationToken,
        MotionAxis? axis = null)
    {
        // Admission and HOME startup read the synchronous SDK before the first asynchronous wait.
        await Task.Run(async () =>
        {
            var activeToken = cancellationToken;
            try
            {
                if (_state.IsRunningFor())
                    return;
                var homingAxes = false;
                using var operation = BeginManualOperation(
                    () => IsHomeAxisReady(group, axis, requireRaised: homingAxes),
                    cancellationToken);
                if (operation is null)
                    return;
                activeToken = operation.Token;
                operation.Token.ThrowIfCancellationRequested();
                _state.IsHoming = true;
                try
                {
                    await RaiseCylindersAsync(operation, group);
                    homingAxes = true;
                    if (!IsHomeAxisReady(group, axis, requireRaised: true))
                        return;
                    bool homed;
                    switch (group)
                    {
                        case MotionGroup.PcbSupply:
                            homed = await _pcbSupply.HomeAxisAsync(axis ?? MotionAxis.Z, operation.Token);
                            if (homed && axis is null)
                                homed = await _pcbSupply.HomeHorizontalAsync(operation.Token);
                            break;
                        case MotionGroup.PcbPlacementHandler:
                            homed = await _pcbPlacement.HomeAxisAsync(axis ?? MotionAxis.Z, operation.Token);
                            if (homed && axis is null)
                                homed = await _pcbPlacement.HomeHorizontalAsync(operation.Token);
                            break;
                        case MotionGroup.BoltFastening:
                            homed = await _fasteningStation.HomeAxisAsync(axis ?? MotionAxis.Z, operation.Token);
                            if (homed && axis is null)
                                homed = await _fasteningStation.HomeHorizontalAsync(operation.Token);
                            break;
                        case MotionGroup.InspectionGantry:
                            homed = axis is { } selectedAxis
                                ? await _inspectionStation.HomeAxisAsync(selectedAxis, operation.Token)
                                : await _inspectionStation.HomeHorizontalAsync(operation.Token);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(group));
                    }

                    if (!homed && !operation.Token.IsCancellationRequested)
                        _state.SetError(MachineAlarm.HomeFailed);
                }
                finally
                {
                    _state.IsHoming = false;
                    _state.Refresh();
                }
            }
            catch (OperationCanceledException) when (activeToken.IsCancellationRequested
                || _operations.IsShuttingDown)
            {
            }
            catch (Exception exception)
            {
                ReportManualFailure(MachineAlarm.HomeFailed, exception);
            }
        });
    }

    private async Task RaiseCylindersAsync(
        OperationCancellation.Operation operation, MotionGroup? group = null)
    {
        async Task ObserveRaiseAsync(Task raising, MachineAlarm alarm)
        {
            try
            {
                await raising;
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (!_state.IsError)
                    _state.SetError(alarm, exception);
                else
                    _log?.LogError(exception, "{Message}", $"Cylinder raise {alarm} failed while stopping; existing alarm={_state.Alarm}.");
                operation.Cancel();
            }
            operation.Token.ThrowIfCancellationRequested();
        }

        operation.Token.ThrowIfCancellationRequested();
        if (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
            || group is null && PcbHandlersEnabled)
        {
            // Nearby material may still need support. Presence conservatively inhibits IPM lifting;
            // it does not establish PCB grip or advance the automatic sequence.
            var pcbDetected = _io.GetInput(InputIo.PcbPlacementPcbDetected);
            await ObserveRaiseAsync(
                _pcbPlacement.SetLiftDownAsync(false, operation.Token), MachineAlarm.PcbPlacement);
            if (!pcbDetected && !_io.GetInput(InputIo.PcbPlacementPcbDetected))
                await ObserveRaiseAsync(
                    _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, operation.Token), MachineAlarm.PcbPlacement);
        }
        if (group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
            await ObserveRaiseAsync(_fasteningStation.RaiseCylindersAsync(operation.Token), MachineAlarm.BoltFastening);
        if (group == MotionGroup.InspectionGantry || group is null && InspectionGantryEnabled)
            await ObserveRaiseAsync(
                _inspectionStation.SetLiftUpAsync(true, operation.Token), MachineAlarm.NgCarrierTransfer);
        operation.Token.ThrowIfCancellationRequested();
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() => HomeAllAxesAsync(cancellationToken), cancellationToken);
    }

    private async Task HomeAllAxesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsHomeAllowedFor(_state.MotionReadiness))
                return;
        }
        catch (Exception exception) when (exception is IOException or MotionException)
        {
            StopAndReportFailure(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            return;
        }

        using var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            return;
        cancellationToken = operation.Token;
        var homingAxes = false;
        void StopWhenHomeBecomesUnavailable()
        {
            if (operation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (!_io.IsReady
                    || _state.IsError
                    || !_state.SafetyReady
                    || GetHomeBlock(requireRaised: homingAxes) != HomeBlockReason.None)
                {
                    operation.Cancel();
                    return;
                }

                var motion = _state.MotionReadiness;
                if (motion.Faulted || !motion.ServosOn || !_state.ServoMainContactorOn)
                {
                    operation.Cancel();
                    _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable);
                }
            }
            catch (Exception exception)
            {
                operation.Cancel();
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            }
        }

        _state.Changed += StopWhenHomeBecomesUnavailable;
        try
        {
            _state.IsHoming = true;
            await RaiseCylindersAsync(operation);
            homingAxes = true;
            StopWhenHomeBecomesUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(
                _units.PcbPlacement
                    ? CheckHomeAsync(
                        _pcbPlacement.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.PcbSupply
                    ? CheckHomeAsync(
                        _pcbSupply.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.BoltFastening
                    ? CheckHomeAsync(
                        _fasteningStation.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                    : Task.CompletedTask);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(
                _units.PcbPlacement
                    ? CheckHomeAsync(_pcbPlacement.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.PcbSupply
                    ? CheckHomeAsync(_pcbSupply.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.BoltFastening
                    ? CheckHomeAsync(_fasteningStation.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                InspectionGantryEnabled
                    ? CheckHomeAsync(_inspectionStation.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm
                : exception is IOException ? MachineAlarm.IoCommunication : MachineAlarm.HomeFailed, exception);
        }
        finally
        {
            _state.Changed -= StopWhenHomeBecomesUnavailable;
            _state.IsHoming = false;
            _state.Refresh();
        }
    }

    private async Task CheckHomeAsync(Task<bool> homing, CancellationToken cancellationToken)
    {
        try
        {
            if (!await homing && !cancellationToken.IsCancellationRequested)
                _state.SetError(MachineAlarm.HomeFailed);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.HomeFailed, exception);
            throw;
        }
    }

    public bool IsUseAdcProtocolAllowed => AdcProtocolAvailable && !_state.IsRunning;

    public bool AdcProtocolAvailable => !_operations.IsShuttingDown && _state.ManualMode && _state.SafetyReady;

    internal bool IsManualMotionReady(MotionGroup group, bool live = true)
    {
        if (_operations.IsShuttingDown
            || !_io.IsReady
            || !_state.ManualMode
            || !_state.SafetyReady
            || !_units.IsMotionEnabled(group))
            return false;
        var motion = _state.GetMotionStatus(group);
        return (!live || motion.Feedback.IsReady)
            && motion.Feedback.Axes.All(
                axis =>
                    (live ? motion.Feedback.GetAxisState(axis) : motion.Axes[axis].State)
                        is { Homed: true, ServoOn: true, Alarm: false, Emergency: false });
    }

    internal MachineAlarm GetMotionAlarm(MotionGroup group)
    {
        switch (group)
        {
            case MotionGroup.PcbSupply:
                return MachineAlarm.PcbSupply;
            case MotionGroup.PcbPlacementHandler:
                return MachineAlarm.PcbPlacement;
            case MotionGroup.BoltFastening:
                return MachineAlarm.BoltFastening;
            case MotionGroup.InspectionGantry:
                return MachineAlarm.Inspection;
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }
    }

    // Acquires ownership and watches availability. The caller executes the device command.
    internal OperationCancellation.Operation? BeginManualOperation(
        Func<bool> available,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation = default)
    {
        var operation = _operations.TryBegin(cancellationToken, viewCancellation);
        if (operation is null)
            return null;

        void StopWhenUnavailable()
        {
            if (!operation.IsCancellationRequested && !available())
                operation.Cancel();
        }

        _state.Changed += StopWhenUnavailable;
        operation.Disposed += () => _state.Changed -= StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    internal static bool IsDeviceFailure(Exception exception)
    {
        return exception is IOException or MotionException or MotionInterlockException or IoTimeoutException
            || exception is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.Any(
                    error => error is IOException or MotionException or MotionInterlockException or IoTimeoutException);
    }

    internal void ReportManualFailure(MachineAlarm alarm, Exception exception)
    {
        if (_state.IsError)
            alarm = _state.Alarm;
        else if (IsMotionFailure(exception) && alarm != MachineAlarm.HomeFailed)
            alarm = MachineAlarm.MotionUnavailable;

        if (exception is IoTimeoutException)
            _state.SetError(alarm, exception);
        else
            StopAndReportFailure(alarm, exception);
    }

    internal bool IsSetServoAllowed(MotionGroup group, bool live = true)
    {
        return _units.IsMotionEnabled(group)
            && (live
                ? _state.ManualMode && _state.SafetyReady && !_state.IsRunningFor()
                : _state.Available
                    && !_state.AutoMode
                    && _state.SafetyReady
                    && !_state.IsRunning)
            && (live
                ? _state.GetMotionStatus(group).Feedback.IsReady
                : _state.GetMotionStatus(group).Axes.Values.All(axis => axis.State is not null));
    }

    internal void ToggleServo(MotionGroup group, MotionAxis axis)
    {
        try
        {
            if (!IsSetServoAllowed(group))
                return;
            using var operation = _operations.TryBegin();
            if (operation is null)
                return;
            var on = !_state.GetMotionStatus(group).Feedback.GetAxisState(axis).ServoOn;
            _motions[group].SetServo(axis, on);
        }
        catch (Exception exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            _operations.Cancel();
        }
    }

    internal OperationCancellation.Operation BeginAdcProtocol(CancellationToken cancellationToken)
    {
        if (!IsUseAdcProtocolAllowed || _state.IsRunningFor())
        {
            throw new InvalidOperationException("ADC diagnostics require an idle machine in manual mode.");
        }

        var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            throw new InvalidOperationException("Another machine operation acquired control before ADC diagnostics started.");
        return operation;
    }

    internal void EnsureBoltTestAvailable()
    {
        if (!AdcProtocolAvailable)
        {
            throw new InvalidOperationException("Bolt testing requires safe manual mode.");
        }

    }

    private Task _resetTask = Task.CompletedTask;

    public bool IsResetAllowed => IsResetAllowedFor(_state.IsRunning);

    private bool IsResetAllowedFor(bool running)
    {
        switch (true)
        {
            case true when _operations.IsShuttingDown
                || _feedback.Failure is not null
                || running:
                return false;
            case true when _state.Alarm == MachineAlarm.IoCommunication:
                return true;
            case true when !_state.SafetyReady || !(_state.ManualMode || _state.DoorInterlockReady):
                return false;
            // Hardware recovery admission, not permission to acknowledge the buzzer.
            // A failed feedback scan must leave recovery usable without another native read.
            case true when _state.IsError
                || _feedback.ReadError is not null:
                return true;
        }
        var motion = _state.FeedbackReadiness;
        return motion.Faulted || !motion.ServosOn || !_state.ServoMainContactorOn;
    }

    public async Task ResetAsync()
    {
        await Task.Run(AcknowledgeAndResetAsync);
    }

    private Task AcknowledgeAndResetAsync()
    {
        SilenceBuzzer();
        lock (_resetGate)
        {
            // Repeated clicks acknowledge the buzzer, but share the current recovery.
            switch (true)
            {
                case true when !_resetTask.IsCompleted:
                    return _resetTask;
                case true when _state.IsError && _operations.HasActiveOperations
                    && !_operations.IsShuttingDown && _feedback.Failure is null:
                    return _resetTask = ResetHardwareAsync();
                case true when !IsResetAllowed:
                    _log?.LogInformation("Machine RESET: buzzer silenced; hardware recovery conditions are not satisfied.");
                    return Task.CompletedTask;
                default:
                    return _resetTask = ResetHardwareAsync();
            }
        }
    }

    private async Task ResetHardwareAsync()
    {
        if (_state.IsError && _operations.HasActiveOperations)
        {
            _log?.LogInformation("Machine RESET: waiting for stopped operation cleanup to finish.");
            await _operations.WaitForIdleAsync();
        }
        try
        {
            // Button availability uses acquired feedback; RESET must recheck the run outputs.
            // Disconnected I/O is initialized below without trying to read it first.
            if (!IsResetAllowedFor(_state.IsRunningFor()))
            {
                _log?.LogInformation("Machine RESET: hardware recovery conditions are not satisfied after the stop check.");
                return;
            }
        }
        catch (IOException exception)
        {
            StopAndReportFailure(MachineAlarm.IoCommunication, exception);
            return;
        }

        _log?.LogInformation("Machine RESET started.");
        using var operation = _operations.TryBegin();
        if (operation is null)
            return;
        var (alarm, error) = await InitializeIoAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        switch (true)
        {
            case true when alarm != MachineAlarm.None:
                _state.SetError(alarm, error);
                return;
            case true when !_state.SafetyReady || !_state.ManualMode && !_state.DoorInterlockReady:
                return;
        }

        var failures = new List<Exception>();
        void RecordFailure(MachineAlarm deviceAlarm, string device, Exception exception)
        {
            _log?.LogError(exception, "{Message}", $"{device} reset failed.");
            if (alarm == MachineAlarm.None)
            {
                alarm = deviceAlarm;
            }

            failures.Add(exception);
        }

        foreach (var group in Enum.GetValues<MotionGroup>())
        {
            operation.Token.ThrowIfCancellationRequested();
            if (!_units.IsMotionEnabled(group))
            {
                continue;
            }

            try
            {
                var motion = _motions[group];
                motion.Initialize();
                operation.Token.ThrowIfCancellationRequested();
                await motion.ResetAsync(operation.Token);
                foreach (var axis in motion.Axes)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    motion.SetServo(axis, true);
                }
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.MotionUnavailable, group.ToString(), exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                await _inspectionStation.InitializeVisionAsync(operation.Token);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.Inspection, "Vision / lighting", exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                await _fasteningStation.ResetHeadsAsync(operation.Token);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.BoltFastening, "Bolt controllers", exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        try
        {
            var motion = _feedback.ReadLiveReadiness();
            if (motion.Faulted || !motion.ServosOn)
                throw new InvalidOperationException(
                    $"Motion reset is not confirmed by hardware feedback: faulted={motion.Faulted}, servosOn={motion.ServosOn}.");
        }
        catch (Exception exception)
        {
            RecordFailure(MachineAlarm.MotionUnavailable, "Motion feedback", exception);
        }
        if (failures.Count > 0)
        {
            _state.SetError(alarm, failures.Count == 1 ? failures[0] : new AggregateException(failures));
            return;
        }

        _state.ClearError();
        _state.Refresh();
        _log?.LogInformation("Machine RESET completed.");
    }

    internal void SilenceBuzzer()
    {
        if (!_io.IsReady)
            return;

        try
        {
            _io.SetOutput(OutputIo.Buzzer, false);
        }
        catch (IOException exception)
        {
            _log?.LogError(exception, "Buzzer OFF failed.");
        }
    }

    // Called by alarm/run/NG notifications, never by the display or acquisition loops.
    private void UpdateMachineIndicators()
    {
        if (!_io.IsReady)
            return;

        try
        {
            var notification = (_state.Alarm, Running: _state.AutomaticRunning, NgAlarm: _ngConveyor.AlarmRequired);
            var previous = _lastIndicatorNotification;
            if (previous == notification)
                return;

            var attention = notification.Alarm != MachineAlarm.None || notification.NgAlarm;
            var newAlarm = notification.Alarm != MachineAlarm.None
                    && notification.Alarm != previous?.Alarm
                || notification.NgAlarm && previous?.NgAlarm != true;

            _io.SetOutput(OutputIo.TowerLampGreen, notification.Running && !attention);
            _io.SetOutput(OutputIo.TowerLampYellow, !notification.Running && !attention);
            _io.SetOutput(OutputIo.TowerLampRed, attention);
            if (!attention || newAlarm)
                _io.SetOutput(OutputIo.Buzzer, newAlarm);

            _lastIndicatorNotification = notification;
        }
        catch (IOException exception)
        {
            _log?.LogError(exception, "Machine indicator output update failed.");
        }
    }

    // OUTPUTS writes just the selected logical output. Feedback is display-only;
    // it does not start a conveyor sequence, move an axis, or wait for a cylinder.
    internal OutputBlockReason ToggleDiagnosticOutput(OutputIo signal)
    {
        var block = ManualOutputSafetyBlock;
        if (block != OutputBlockReason.None)
        {
            _log?.LogInformation("{Message}", $"Direct output {signal} ignored: [{block}] {block.GetDescription()}");
            return block;
        }

        try
        {
            var value = signal switch
            {
                OutputIo.MainConveyorNormalSpeed or OutputIo.NgConveyorNormalSpeed => true,
                OutputIo.PcbPlacementHandlerRotate => false,
                _ => !_io.GetOutput(signal),
            };
            _io.SetOutput(signal, value);
            _log?.LogInformation("{Message}", $"Direct output {signal}: {(value ? "ON" : "OFF")}; alarm={_state.Alarm}.");
            _state.Refresh();
            return OutputBlockReason.None;
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
            throw;
        }
    }

    // Shared minimum conditions for direct I/O and manual conveyor runs.
    private OutputBlockReason ManualOutputSafetyBlock
    {
        get
        {
            switch (true)
            {
                case true when _operations.IsShuttingDown:
                    return OutputBlockReason.ShuttingDown;
                case true when !_io.IsReady:
                    return OutputBlockReason.IoUnavailable;
                case true when !_state.ManualMode:
                    return OutputBlockReason.AutoMode;
                case true when !_state.EmergencyStopReleased:
                    return OutputBlockReason.EmergencyStop;
                default:
                    return OutputBlockReason.None;
            }
        }
    }

    internal async Task StopManualConveyorAsync(OutputIo signal)
    {
        await Task.Run(() =>
        {
            try
            {
                if (signal == OutputIo.MainConveyorRun)
                    _conveyor.Stop();
                else if (signal == OutputIo.NgConveyorRun)
                    _ngConveyor.Stop();
                else
                    throw new ArgumentOutOfRangeException(nameof(signal));
            }
            catch (Exception exception)
            {
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, exception);
                _operations.Cancel();
            }
        });
    }

    internal async Task<OutputBlockReason> RunManualConveyorAsync(
        OutputIo signal,
        CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = ManualOutputSafetyBlock;
            if (block != OutputBlockReason.None)
            {
                _log?.LogInformation("{Message}", $"Manual conveyor {signal} ignored: [{block}] {block.GetDescription()}");
                return block;
            }

            operation = _operations.TryBegin(cancellationToken);
            if (operation is null)
                return OutputBlockReason.Busy;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
            throw;
        }

        var stopReason = OutputBlockReason.None;
        Exception? failure = null;
        using (operation)
        {
            var outputStarted = false;

            void StopWhenUnavailable()
            {
                if (operation.IsCancellationRequested)
                    return;
                try
                {
                    var reason = ManualOutputSafetyBlock;
                    if (reason != OutputBlockReason.None)
                    {
                        stopReason = reason;
                        operation.Cancel();
                        _log?.LogInformation("{Message}", $"Manual conveyor {signal} stopped: [{reason}] {reason.GetDescription()}");
                    }
                }
                catch (Exception exception)
                {
                    // This callback runs on the I/O worker. Cancel without stopping its scan.
                    _log?.LogError(exception, "{Message}", $"Manual conveyor {signal}: interlock feedback could not be read.");
                    operation.Cancel();
                }
            }

            void OnOutputChanged(OutputIo output, bool on)
            {
                // OFF from OUTPUTS must release the Manual operation too.
                if (outputStarted && output == signal && !on)
                    operation.Cancel();
            }

            _state.Changed += StopWhenUnavailable;
            _io.OutputChanged += OnOutputChanged;
            Task motorRun = Task.CompletedTask;
            try
            {
                StopWhenUnavailable();
                operation.Token.ThrowIfCancellationRequested();
                _log?.LogInformation("{Message}", $"Manual conveyor {signal}: ON, forward; alarm={_state.Alarm}.");
                if (signal == OutputIo.MainConveyorRun)
                {
                    motorRun = _conveyor.RunMotorAsync(operation.Token);
                }
                else if (signal == OutputIo.NgConveyorRun)
                {
                    motorRun = _ngConveyor.RunMotorAsync(operation.Token);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(signal));
                }

                outputStarted = true;
                if (!_io.GetOutput(signal))
                    operation.Cancel();
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            // A read failure after starting must still cancel and await the device's cleanup.
            try
            {
                if (failure is not null && !operation.IsCancellationRequested)
                    operation.Cancel();
            }
            catch (Exception exception)
            {
                failure = new AggregateException(failure!, exception);
            }

            try
            {
                await motorRun.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
                _io.OutputChanged -= OnOutputChanged;
            }

            if (failure is null && outputStarted)
                _log?.LogInformation("{Message}", $"Manual conveyor {signal}: OFF.");
        }

        if (failure is not null)
        {
            if (failure is not OperationCanceledException)
            {
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, failure);
                _operations.Cancel();
            }
            ExceptionDispatchInfo.Throw(failure);
        }

        return stopReason;
    }

    // Teaching may coordinate a handler as well as its cylinder output.
    internal bool IsSetTeachingOutputAllowed(TeachingOutput output, bool live = true)
    {
        return (_state.ManualSetupEnabled && (!live || !_state.IsRunningFor()))
            && output.Signal != OutputIo.PcbPlacementHandlerRotate
            && (output.Signal != OutputIo.PcbSupplyRotate
                || IsManualMotionReady(MotionGroup.PcbSupply, live));
    }

    internal async Task ToggleTeachingOutputAsync(
        TeachingOutput output,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        var viewToken = viewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!IsSetTeachingOutputAllowed(output))
                return;
            using var operation = BeginManualOperation(
                () => _io.IsReady && _state.ManualMode && _state.SafetyReady,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            var value = !_io.GetOutput(output.Signal);
            if (output.Signal == OutputIo.ShootBolt)
            {
                _io.SetOutput(output.Signal, value);
                return;
            }
            switch (output.Signal)
            {
                case OutputIo.PcbSupplyRotate:
                    await _pcbSupply.SetRotatedAsync(value, operation.Token);
                    break;
                case OutputIo.PcbPlacementHandlerDown:
                    await _pcbPlacement.SetLiftDownAsync(value, operation.Token);
                    break;
                case OutputIo.PcbPlacementVacuumEjector:
                    await _pcbPlacement.SetVacuumAsync(value, operation.Token);
                    break;
                case OutputIo.PickupHeadDown:
                    await _fasteningStation.SetHeadDownAsync(FasteningHead.Pickup, value, operation.Token);
                    break;
                case OutputIo.ShootingHeadDown:
                    await _fasteningStation.SetHeadDownAsync(FasteningHead.Shooting, value, operation.Token);
                    break;
                case OutputIo.PickupHeadVacuumPump:
                    await _fasteningStation.SetVacuumAsync(FasteningHead.Pickup, value, operation.Token);
                    break;
                case OutputIo.ShootingHeadVacuumPump:
                    await _fasteningStation.SetVacuumAsync(FasteningHead.Shooting, value, operation.Token);
                    break;
                case OutputIo.NgCarrierPickupDown:
                    await _inspectionStation.SetLiftUpAsync(!value, operation.Token);
                    break;
                case OutputIo.NgCarrierGripperClose:
                    await _inspectionStation.SetGripperOpenAsync(!value, operation.Token);
                    break;
                case OutputIo.NgShuttleDown:
                    await _ngConveyor.SetShuttleDownAsync(value, operation.Token);
                    break;
                case OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward or OutputIo.PcbPlacementIpmDown:
                case OutputIo.PickupTableDown:
                case OutputIo.PcbPlacementBackupPlateUp:
                case OutputIo.BoltFasteningBackupPlateUp:
                case OutputIo.InspectionBackupPlateUp:
                case OutputIo.PcbPlacementStopperUp:
                case OutputIo.BoltFasteningStopperUp:
                case OutputIo.InspectionStopperUp:
                    await _io.SetOutputAndWaitAsync(output.Signal, value, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(output));
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || _operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            ReportManualFailure(output.Owner switch
            {
                HardwareArea.MainConveyor => MachineAlarm.MainConveyor,
                HardwareArea.PcbSupply => MachineAlarm.PcbSupply,
                HardwareArea.PcbPlacementHandler => MachineAlarm.PcbPlacement,
                HardwareArea.BoltFastening => MachineAlarm.BoltFastening,
                HardwareArea.NgCarrierTransfer => MachineAlarm.NgCarrierTransfer,
                HardwareArea.NgShuttle => MachineAlarm.NgShuttle,
                _ => throw new ArgumentOutOfRangeException(nameof(output)),
            }, exception);
        }
    }
}
