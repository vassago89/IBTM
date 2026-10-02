using System;
using System.Collections.Concurrent;
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
    private readonly PcbPlacementHandlerSettings _placementSettings;
    private readonly BoltFasteningSettings _fasteningSettings;
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
    private Task _resetTask;
    private readonly ConcurrentQueue<(MachineAlarm Alarm, bool Running, bool NgAlarm, bool NgEjectionPending, bool SilenceBuzzer)> _indicatorNotifications;
    // Last handled notification, not the physical lamp/buzzer state.
    private (MachineAlarm Alarm, bool Running, bool NgAlarm, bool NgEjectionPending)? _lastIndicatorNotification;
    // Device callbacks must not wait for the writer that may be using their device.
    private int _indicatorWriterActive;

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
        PcbPlacementHandlerSettings placementSettings,
        BoltFasteningSettings fasteningSettings,
        RecipeManager recipes,
        IIoService io,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        PcbSupplier pcbSupply,
        PcbPlacer pcbPlacement,
        BoltFasteningStation fasteningStation,
        InspectionStation inspectionStation,
        BoltFeederUnit boltFeeder,
        PcbHistoryWriter pcbHistory,
        IReadOnlyDictionary<MotionGroup, IXyMotion> motions,
        ILogger<MachineController>? log = null)
    {
        _resetGate = new();
        _resetTask = Task.CompletedTask;
        _indicatorNotifications = new();
        StartChecks = Enum.GetValues<StartArea>().ToDictionary(area => area, area => StartCheckState.NotChecked);

        _state = state;
        _feedback = feedback;
        _operations = operations;
        _options = options;
        _units = units;
        _placementSettings = placementSettings;
        _fasteningSettings = fasteningSettings;
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
        feedback.Sampled += OnMotionFeedbackSampled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PcbHistoryWriter PcbHistory { get; }

    private void OnRecipeChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(StartBlock)));
        PropertyChanged?.Invoke(this, new(nameof(IsStartAllowed)));
    }

    private bool PcbHandlersEnabled => _units.PcbSupply || _units.PcbPlacement;

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
        var readinessChanged = e.PropertyName is null or nameof(MachineState.Ready)
            or nameof(MachineState.SafetyReady) or nameof(MachineState.DoorInterlockReady)
            or nameof(MachineState.Alarm) or nameof(MachineState.RepeatEnabled);
        if (readinessChanged)
            PropertyChanged?.Invoke(this, new(nameof(StartBlock)));
        if (readinessChanged || e.PropertyName == nameof(MachineState.IsRunning))
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
        if (e.PropertyName is nameof(MachineState.Alarm) or nameof(MachineState.AutomaticRunning)
            or nameof(MachineState.FeedbackReadiness))
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

    private void OnMotionFeedbackSampled(MotionGroup group, MotionFeedbackSample sample)
    {
        // Check every moving sample: XY can start while Z is already moving,
        // without a local command or a change to the combined IsMoving value.
        if (sample is { Enabled: true, IoReady: true, ReadError: null }
            && group is MotionGroup.PcbPlacementHandler or MotionGroup.BoltFastening or MotionGroup.InspectionGantry
            && _feedback.Motions[group].IsMoving)
            CheckMotionInterlocks();
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
            && _pcbPlacement.Lift != StationCylinderState.Up
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
        else if (_units.Inspection
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
            if (_options.UseEmergencyStop && !_state.EmergencyStopReleased)
                return MachineAlarm.EmergencyStop;
            if (!_state.DoorInterlockReady)
                return MachineAlarm.DoorOpen;
            return _options.UseAirPressureInterlock && !_state.AirPressureOk
                ? MachineAlarm.AirPressureLow
                : MachineAlarm.None;
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
            if (alarm == MachineAlarm.None)
            {
                operation.Token.ThrowIfCancellationRequested();
                alarm = SafetyAlarm;
            }

            if (alarm == MachineAlarm.None)
                _state.Refresh();
            else
                _state.SetError(alarm, error);
        }

        UpdateMachineIndicators();
        _log?.LogInformation("Machine initialization finished. Alarm={Alarm}.", _state.Alarm.ToString());
    }

    private async Task InitializeIoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log?.LogInformation("Control I/O initialization started.");
        // Keep SDK initialization off the input notification thread.
        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _io.Initialize();
                cancellationToken.ThrowIfCancellationRequested();
                _log?.LogInformation("Control I/O readiness check started.");
                _io.CheckReady();
            },
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        StopRunOutputs();
        _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
        _io.SetOutput(OutputIo.PcbPlacementHandlerRotate, false);
        _io.SetOutput(OutputIo.MainConveyorForward, true);
        _log?.LogInformation("Control I/O initialization and readiness check completed.");
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeHardwareAsync(
        CancellationToken cancellationToken)
    {
        var alarm = MachineAlarm.IoCommunication;
        try
        {
            try
            {
                await InitializeIoAsync(cancellationToken);
            }
            finally
            {
                // Keep input feedback available for recovery even if initialization fails.
                if (!cancellationToken.IsCancellationRequested)
                    await _feedback.StartAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            alarm = MachineAlarm.MotionUnavailable;
            if (_units.PcbSupply)
            {
                _log?.LogInformation("PCB supply motion initialization started.");
                _motions[MotionGroup.PcbSupply].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (_units.PcbPlacement)
            {
                _log?.LogInformation("PCB placement motion initialization started.");
                _motions[MotionGroup.PcbPlacementHandler].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (_units.BoltFastening)
            {
                _log?.LogInformation("Bolt fastening motion initialization started.");
                _motions[MotionGroup.BoltFastening].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (_units.Inspection)
            {
                _log?.LogInformation("Inspection motion initialization started.");
                _motions[MotionGroup.InspectionGantry].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }
            _log?.LogInformation("Motion initialization completed.");

            cancellationToken.ThrowIfCancellationRequested();
            if (_units.Inspection)
            {
                alarm = MachineAlarm.Inspection;
                _log?.LogInformation("Vision / lighting initialization started.");
                await _inspectionStation.InitializeVisionAsync(cancellationToken);
                _log?.LogInformation("Vision / lighting initialization completed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_units.BoltFastening)
            {
                alarm = MachineAlarm.BoltFastening;
                _log?.LogInformation("Bolt controller readiness check started.");
                await _fasteningStation.CheckReadyAsync(cancellationToken);
                _log?.LogInformation("Bolt controller readiness check completed.");
            }
            return (MachineAlarm.None, null);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (alarm, exception);
        }
    }

    // Keep START available to recheck material after the operator removes it.
    public bool IsStartAllowed => _state.Available
        && IsStartAllowedFor(GetStartBlock(_state.FeedbackReadiness), _state.IsRunning);

    public StartBlockReason StartBlock
    {
        get
        {
            var block = GetStartBlock(_state.FeedbackReadiness);
            if (block != StartBlockReason.None)
                return block;
            if (StartChecks.Values.Contains(StartCheckState.Unknown))
                return StartBlockReason.IoUnavailable;
            if (StartChecks.Values.Contains(StartCheckState.MaterialRemaining))
                return StartBlockReason.MaterialRemaining;
            return StartChecks.Values.Contains(StartCheckState.UnfinishedCarrier)
                ? StartBlockReason.UnfinishedCarrier : StartBlockReason.None;
        }
    }

    // Last explicit check only. Sensor edges do not clear blocked work.
    public IReadOnlyDictionary<StartArea, StartCheckState> StartChecks { get; private set; }

    private IReadOnlyDictionary<StartArea, StartCheckState> CurrentStartMaterials
    {
        get
        {
            var checks = Enum.GetValues<StartArea>().ToDictionary(area => area, area => StartCheckState.Unknown);
            if (_io.IsReady)
            {
                checks[StartArea.Supply] = !_io.GetInput(InputIo.PcbSupplyPcbDetected) ? StartCheckState.Empty
                    : !_state.RepeatEnabled && _pcbSupply.IsHandoffRestartAllowed
                        ? StartCheckState.HandoffReady : StartCheckState.MaterialRemaining;
                checks[StartArea.Placement] = _io.GetInput(InputIo.PcbPlacementPcbDetected)
                    || _io.GetInput(InputIo.PcbPlacementVacuumDetected)
                        ? StartCheckState.MaterialRemaining : StartCheckState.Empty;
                checks[StartArea.PickupHead] = _io.GetInput(InputIo.PickupHeadVacuumDetected)
                    ? StartCheckState.MaterialRemaining : StartCheckState.Empty;
                checks[StartArea.ShootingHead] = _io.GetInput(InputIo.ShootingHeadVacuumDetected)
                    || _io.GetInput(InputIo.ShootingTubeBoltDetected)
                        ? StartCheckState.MaterialRemaining : StartCheckState.Empty;
                checks[StartArea.Station1] = !_pcbPlacement.Station.CarrierPresent ? StartCheckState.Empty
                    : _pcbPlacement.Station.Completed ? StartCheckState.Completed
                    : _pcbPlacement.Station.IsRestartAllowed ? StartCheckState.ReworkReady : StartCheckState.UnfinishedCarrier;
                checks[StartArea.Station2] = !_fasteningStation.Station.CarrierPresent ? StartCheckState.Empty
                    : _fasteningStation.Station.Completed ? StartCheckState.Completed
                    : _fasteningStation.Station.IsRestartAllowed ? StartCheckState.ReworkReady : StartCheckState.UnfinishedCarrier;
                checks[StartArea.Station3] = !_inspectionStation.Station.CarrierPresent ? StartCheckState.Empty
                    : _inspectionStation.Station.Completed ? StartCheckState.Completed
                    : _inspectionStation.Station.IsRestartAllowed ? StartCheckState.ReworkReady : StartCheckState.UnfinishedCarrier;
            }
            return checks;
        }
    }

    public void CheckStartMaterials()
    {
        StartChecks = CurrentStartMaterials;
        PropertyChanged?.Invoke(this, new(nameof(StartChecks)));
        PropertyChanged?.Invoke(this, new(nameof(StartBlock)));
    }

    public void ChangeCarrierWork(
        StartArea area, ConveyorStation.Job job, CarrierWorkAction action, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin(cancellationToken)
            ?? throw new InvalidOperationException("Stop the machine before changing carrier work.");
        if (!_state.Available || _state.IsRunningFor(includeOperations: false))
            throw new InvalidOperationException("Stop the machine and check I/O before changing carrier work.");
        var station = area switch
        {
            StartArea.Station1 => _pcbPlacement.Station,
            StartArea.Station2 => _fasteningStation.Station,
            StartArea.Station3 => _inspectionStation.Station,
            _ => throw new ArgumentOutOfRangeException(nameof(area)),
        };
        station.RequireCurrentJob(job);
        operation.Token.ThrowIfCancellationRequested();
        if (!station.CarrierPresent)
            throw new InvalidOperationException("No carrier is detected at this station.");
        switch (action)
        {
            case CarrierWorkAction.Complete:
                // This skips station work, but never manufactures a passing quality result.
                station.Complete(job);
                break;
            case CarrierWorkAction.Clear:
                foreach (var assembly in station.Assemblies)
                {
                    switch (area)
                    {
                        case StartArea.Station2:
                            assembly.ClearFasteningResults();
                            break;
                        case StartArea.Station3:
                            assembly.ClearInspectionResults();
                            break;
                    }
                }
                station.Restart(job, allowStart: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
        _log?.LogWarning("Operator changed carrier work: {Area}, job={Job}, action={Action}.", area, job.Id, action);
        CheckStartMaterials();
    }

    public bool TeachingReady
    {
        get
        {
            if ((_units.BoltFastening || _units.Inspection)
                && _recipes.Current.Pcb.BoltPoints.Count == 0)
                return false;
            if (_units.BoltFastening
                && _recipes.Current.Pcb.BoltPoints.Any(bolt => !bolt.IsFasteningPositionDefined))
                return false;
            return !_units.Inspection
                || _recipes.Current.Pcb.BoltPoints.All(_inspectionStation.HasRegion)
                    && Enum.GetValues<HeatSinkSlot>().All(_inspectionStation.HasBarcodeRegion);
        }
    }

    private bool IsStartAllowedFor(StartBlockReason block, bool running)
    {
        return !_operations.IsShuttingDown
            && !running
            && block == StartBlockReason.None;
    }

    private StartBlockReason GetStartBlock(MotionReadiness motion)
    {
        if (_state.Alarm == MachineAlarm.EmergencyStop)
            return StartBlockReason.EmergencyStop;
        if (_state.Alarm == MachineAlarm.DoorOpen)
            return StartBlockReason.DoorOpen;
        if (_state.Alarm == MachineAlarm.AirPressureLow)
            return StartBlockReason.AirPressure;
        if (_state.IsError)
            return StartBlockReason.Alarm;
        if (_options.UseEmergencyStop && !_state.EmergencyStopReleased)
            return StartBlockReason.EmergencyStop;
        if (_options.UseAirPressureInterlock && !_state.AirPressureOk)
            return StartBlockReason.AirPressure;
        if (motion.Faulted)
            return StartBlockReason.MotionFault;
        if (!motion.ServosOn || !_state.ServoMainContactorOn)
            return StartBlockReason.ServoOff;
        if (!_state.DoorInterlockReady)
            return StartBlockReason.DoorOpen;
        if (!motion.Homed)
            return StartBlockReason.HomeRequired;
        if (_state.RepeatEnabled && !_state.ManualMode)
            return StartBlockReason.TeachingMode;
        if (!TeachingReady)
            return StartBlockReason.TeachingIncomplete;
        return _units.IsAnyUnitEnabled ? StartBlockReason.None : StartBlockReason.NoUnitEnabled;
    }

    public bool IsFasteningResumeAllowed(ConveyorStation.Job job)
    {
        return _units.BoltFastening && !_state.RepeatEnabled
            && ReferenceEquals(job, _fasteningStation.Station.CurrentJob)
            && _fasteningStation.Station.CarrierSeated && !_fasteningStation.Station.Completed;
    }

    public async Task StartAsync(
        CancellationToken cancellationToken = default, ConveyorStation.Job? resumeFastening = null)
    {
        await Task.Run(async () =>
        {
            var failureAlarm = MachineAlarm.MotionUnavailable;
            OperationCancellation.Operation? operation = null;
            try
            {
                operation = _operations.TryBegin(cancellationToken);
                if (operation is null)
                    return;
                cancellationToken = operation.Token;
                if (!IsStartAllowedFor(
                    GetStartBlock(_feedback.ReadLiveReadiness()), _state.IsRunningFor(includeOperations: false)))
                    return;
                operation.Token.ThrowIfCancellationRequested();

                CheckStartMaterials();
                if (resumeFastening is not null && !IsFasteningResumeAllowed(resumeFastening))
                    return;
                if (StartChecks.Any(check => check.Value is StartCheckState.Unknown or StartCheckState.MaterialRemaining
                    || check.Value == StartCheckState.UnfinishedCarrier
                        && !(check.Key == StartArea.Station2 && resumeFastening is not null)))
                    return;

                // Only an accepted START clears work from stations that are now empty.
                if (!_pcbPlacement.Station.CarrierPresent)
                    _pcbPlacement.Station.ClearJob();
                if (!_fasteningStation.Station.CarrierPresent)
                    _fasteningStation.Station.ClearJob();
                if (!_inspectionStation.Station.CarrierPresent)
                    _inspectionStation.Station.ClearJob();

                // Clear is a one-start acknowledgement, not permission to resume after another stop.
                foreach (var station in new[] { _pcbPlacement.Station, _fasteningStation.Station, _inspectionStation.Station })
                    if (station.IsRestartAllowed)
                        station.Restart(station.CurrentJob);

                var repeat = _state.RepeatEnabled;
                var startedInManual = _state.ManualMode;
                var feedbackStartedAt = Stopwatch.GetTimestamp();
                void StopWhenOperationBecomesUnavailable()
                {
                    if (operation.IsCancellationRequested)
                        return;
                    if (_state.IsError)
                    {
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
                            // Motion faults are handled by the acquisition sample below.
                            // DI/state notifications must not start another full native scan.
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
                    if (operation.IsCancellationRequested
                        || sample.StartedAt < feedbackStartedAt
                        || !sample.Enabled
                        || !_units.IsMotionEnabled(group))
                        return;
                    if (_state.IsError)
                    {
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
                    if (startAlarm != MachineAlarm.None)
                    {
                        _state.SetError(startAlarm, startError);
                        return;
                    }
                    operation.Token.ThrowIfCancellationRequested();

                    _state.Changed += StopWhenOperationBecomesUnavailable;
                    _feedback.Sampled += StopWhenMotionFeedbackBecomesUnavailable;
                    StopWhenOperationBecomesUnavailable();
                    if (operation.IsCancellationRequested)
                    {
                        return;
                    }

                    // Initialization can change drive feedback. Recheck once before motion;
                    // ongoing supervision then belongs to the shared acquisition loop.
                    var motion = _feedback.ReadLiveReadiness();
                    operation.Token.ThrowIfCancellationRequested();
                    if (!motion.Homed || !motion.ServosOn || motion.Faulted)
                    {
                        _state.SetError(MachineAlarm.MotionUnavailable);
                        return;
                    }

                    await RaiseCylindersAsync(operation);

                    if (_units.MainConveyor)
                    {
                        failureAlarm = MachineAlarm.MainConveyor;
                        await _conveyor.PrepareEmptyStationsAsync(operation.Token);
                    }

                    operation.Token.ThrowIfCancellationRequested();
                    failureAlarm = MachineAlarm.IoCommunication;
                    _state.AutomaticRunning = true;
                    if (repeat)
                    {
                        await RunRepeatAsync(operation.Token);
                    }
                    else
                    {
                        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                        await RunAutomaticUnitsAsync(cycle, repeat: false, resumeFastening);
                    }
                }
                finally
                {
                    _state.Changed -= StopWhenOperationBecomesUnavailable;
                    _feedback.Sampled -= StopWhenMotionFeedbackBecomesUnavailable;
                    _state.AutomaticRunning = false;
                    _inspectionStation.ClearInspectionRequest();
                    try
                    {
                        StopAndReportFailure();
                        if (_state.PendingStop is { } pending && !_state.IsError)
                            _state.SetError(pending.Alarm, pending.Error);
                    }
                    finally
                    {
                        _state.PendingStop = null;
                    }
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _operations.IsShuttingDown)
            {
            }
            catch (Exception exception)
            {
                StopAndReportFailure(_state.IsError ? _state.Alarm : failureAlarm, exception);
            }
            finally
            {
                operation?.Dispose();
            }
        });
    }

    private async Task RunAutomaticUnitsAsync(
        CancellationTokenSource cycle, bool repeat, ConveyorStation.Job? resumeFastening = null)
    {
        var runningUnits = new List<Task>();
        AutoUnit[] workUnits = [_conveyor, _pcbSupply, _pcbPlacement,
            _fasteningStation, _inspectionStation, _ngConveyor];
        var changed = new AsyncAutoResetEvent();
        void OnFeedbackSampled(MotionGroup group, MotionFeedbackSample sample)
        {
            changed.Set();
        }
        foreach (var unit in workUnits)
        {
            unit.Changed += changed.Set;
            unit.StepChanged += changed.Set;
        }
        _state.Changed += changed.Set;
        _feedback.Sampled += OnFeedbackSampled;
        try
        {
            if (!cycle.IsCancellationRequested && _units.PcbSupply && (!repeat || _units.PcbPlacement))
            {
                runningUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.PcbSupply,
                    _pcbSupply.RunAsync(_pcbPlacement, cycle.Token, repeat),
                    cycle, repeat));
            }

            if (!cycle.IsCancellationRequested)
            {
                runningUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.PcbPlacement,
                    _pcbPlacement.RunAsync(cycle.Token, repeat),
                    cycle, repeat));
            }

            if (!cycle.IsCancellationRequested && !repeat
                && (_units.PickupBoltFeeder || _units.ShootingBoltFeeder))
            {
                runningUnits.Add(ObserveAutomaticUnitAsync(
                    _units.ShootingBoltFeeder ? MachineAlarm.ShootingBoltFeeder : MachineAlarm.PickupBoltFeeder,
                    _boltFeeder.RunAsync(cycle.Token),
                    cycle, repeat));
            }

            if (!cycle.IsCancellationRequested)
            {
                Guid[]? remainingBolts = null;
                if (resumeFastening is not null)
                {
                    _fasteningStation.Station.RequireCurrentJob(resumeFastening);
                    remainingBolts = _recipes.Current.Pcb.FasteningPoints
                        .Where(bolt => _fasteningStation.Station.IsHeatSinkPresent(bolt.HeatSink))
                        .Where(bolt =>
                        {
                            var assembly = _fasteningStation.Station.GetAssembly(resumeFastening, bolt.HeatSink);
                            return !assembly.ShootingBoltResults.ContainsKey(bolt.Id)
                                && !assembly.PickupBoltResults.ContainsKey(bolt.Id);
                        }).Select(bolt => bolt.Id).ToArray();
                    _log?.LogInformation("Operator confirmed fastening resume: job={Job}, remaining={Count}.",
                        resumeFastening.Id, remainingBolts.Length);
                }
                if (_units.BoltFastening && (repeat || !_units.PickupBoltFeeder))
                    _log?.LogInformation(
                        "Pickup bolt feeding is disabled for this run; pickup motion remains active without vacuum ON or bolt detection waits. The motor runs for the configured dry-run duration, then stops without waiting for a fastening result.");
                if (_units.BoltFastening && (repeat || !_units.ShootingBoltFeeder))
                    _log?.LogInformation(
                        "Shooting bolt feeding is disabled for this run; bolt supply and shooting are skipped. The motor runs for the configured dry-run duration, then stops without waiting for a fastening result.");
                runningUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.BoltFastening,
                    _fasteningStation.RunAsync(cycle.Token, repeat,
                        selectedBolts: remainingBolts, continueAfterSelection: true),
                    cycle, repeat));
            }

            if (!cycle.IsCancellationRequested)
            {
                runningUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.Inspection,
                    _inspectionStation.RunAsync(cycle.Token, repeat),
                    cycle, repeat));
            }

            if (!cycle.IsCancellationRequested && _units.NgConveyor && !repeat)
            {
                runningUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.NgConveyor,
                    _ngConveyor.RunAsync(cycle.Token, repeat),
                    cycle, repeat));
            }

            // Stations report existing carrier work before the conveyor selects its first transfer.
            if (!cycle.IsCancellationRequested && _units.MainConveyor)
            {
                runningUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.MainConveyor,
                    _conveyor.RunAsync(cycle.Token, repeat),
                    cycle, repeat));
            }

            while (!cycle.IsCancellationRequested)
            {
                // Existing sequence boundaries exclude startup, travel and cleanup between axis moves.
                if (_state.PendingStop is not null && workUnits.All(unit => !unit.IsRunning || unit.Step is
                    PcbSupplyState.WaitingForCarrier or PcbSupplyState.WaitingForCarrierExit or PcbSupplyState.HandingOff
                    or PcbPlacementState.WaitingForCarrier or PcbPlacementState.MovingToHandoff or PcbPlacementState.Disabled
                    or BoltFasteningState.Waiting or BoltFasteningState.Disabled
                    or InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor
                    or InspectionStationState.WaitingForDestination or InspectionStationState.WaitingForShuttleDown
                    or InspectionStationState.Disabled
                    or MainConveyorState.WaitingForFrontCarrier or MainConveyorState.WaitingForRearEquipment
                    or MainConveyorState.WaitingForBoltFastening or MainConveyorState.WaitingForPcbPlacement
                    or MainConveyorState.WaitingForInspectionClear or MainConveyorState.WaitingForInspection
                    or MainConveyorState.WaitingForInspectionTransfer
                    or NgConveyorState.WaitingForCarrier or NgConveyorState.WaitingForTransferRelease
                    or NgConveyorState.WaitingForShuttleDown or NgConveyorState.WaitingForShuttleUp
                    or NgConveyorState.WaitingForEjectConfirmation or NgConveyorState.ReadyToEject or NgConveyorState.Full))
                {
                    // Use START's material rules without updating its explicit operator check.
                    var ready = CurrentStartMaterials.All(check => check.Value switch
                    {
                        StartCheckState.Unknown => false,
                        StartCheckState.MaterialRemaining or StartCheckState.UnfinishedCarrier => check.Key switch
                        {
                            StartArea.Placement or StartArea.Station1 => !_pcbPlacement.IsRunning,
                            StartArea.PickupHead or StartArea.ShootingHead or StartArea.Station2 => !_fasteningStation.IsRunning,
                            _ => false,
                        },
                        _ => true,
                    });
                    if (ready && !_io.GetOutput(OutputIo.MainConveyorRun) && !_io.GetOutput(OutputIo.NgConveyorRun)
                        && _feedback.Motions.All(pair => !_units.IsMotionEnabled(pair.Key)
                            || pair.Value.Feedback.IsReady
                                && MotionServiceBase.IsSettled(pair.Value.Feedback, pair.Value.Feedback.Axes)))
                        break;
                }
                await changed.WaitAsync(cycle.Token);
            }
        }
        catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _state.SetError(IsMotionFailure(exception) ? MachineAlarm.MotionUnavailable : MachineAlarm.IoCommunication, exception);
        }
        finally
        {
            cycle.Cancel();
            try
            {
                await Task.WhenAll(runningUnits).ConfigureAwait(false);
            }
            finally
            {
                foreach (var unit in workUnits)
                {
                    unit.Changed -= changed.Set;
                    unit.StepChanged -= changed.Set;
                }
                _state.Changed -= changed.Set;
                _feedback.Sampled -= OnFeedbackSampled;
                _conveyor.IsTransferPaused = false;
            }
        }
    }

    private async Task ObserveAutomaticUnitAsync(
        MachineAlarm alarm,
        Task running,
        CancellationTokenSource cycle,
        bool repeat = false)
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
            var timeout = exception as IoTimeoutException ?? exception.InnerException as IoTimeoutException;
            if (timeout?.Input == InputIo.PickupFeederBoltDetected)
                alarm = MachineAlarm.PickupBoltFeeder;
            else if (timeout?.Input == InputIo.ShootingFeederBoltDetected)
                alarm = MachineAlarm.ShootingBoltFeeder;
            if (!repeat && (!cycle.IsCancellationRequested || _state.PendingStop is not null)
                && (exception is MaintenanceStopException
                    || exception is IoTimeoutException
                        { Input: InputIo.PickupFeederBoltDetected or InputIo.ShootingFeederBoltDetected }))
            {
                lock (cycle)
                {
                    if (_state.PendingStop is null && !_state.IsError)
                    {
                        _conveyor.IsTransferPaused = true;
                        _state.PendingStop = (alarm, exception);
                        _log?.LogWarning(exception, "Finishing automatic work for maintenance: {Alarm}.", alarm);
                    }
                }
            }
            else if (!_state.IsError)
            {
                // Keep the unit name even when the alarm is classified as MotionUnavailable.
                // SetError records the original exception and its full stack trace.
                _log?.LogError("Automatic unit {Alarm} failed. {Error}", alarm.ToString(), exception.Message);
                _state.SetError(
                    IsMotionFailure(exception) ? MachineAlarm.MotionUnavailable : alarm,
                    exception);
            }
            else
            {
                _log?.LogError(exception,
                    "Automatic unit {UnitAlarm} failed while stopping; existing alarm={Alarm}.",
                    alarm.ToString(), _state.Alarm.ToString());
            }
        }
        finally
        {
            if (_state.PendingStop is null || _state.IsError)
                cycle.Cancel();
        }
    }

    public bool IsHomeAllowed => _state.Available && IsHomeAllowedFor(_state.FeedbackReadiness, _state.IsRunning);

    public HomeBlockReason HomeBlock => GetHomeBlock();

    internal bool IsManualHomeAllowed(MotionGroup group, MotionAxis? axis = null)
    {
        return _state.Available && !_state.IsRunning && IsHomeAxisReady(group, axis, live: false);
    }

    private bool IsHomeAllowedFor(MotionReadiness motion, bool running)
    {
        return !_operations.IsShuttingDown
            && _state.SafetyReady
            && motion.ServosOn
            && !motion.Faulted
            && _state.ServoMainContactorOn
            && !_state.IsError
            && !running
            && HomeBlock == HomeBlockReason.None;
    }

    internal HomeBlockReason GetHomeBlock(MotionGroup? group = null, bool requireRaised = false)
    {
        if (!_io.IsReady)
            return HomeBlockReason.IoUnavailable;
        if (group is { } motionGroup && !_units.IsMotionEnabled(motionGroup))
            return HomeBlockReason.UnitDisabled;
        if (!_state.ManualMode && !_state.DoorInterlockReady)
            return HomeBlockReason.DoorOpen;
        if ((group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
            || group is null && PcbHandlersEnabled)
            && _io.GetInput(InputIo.PcbPlacementPcbDetected)
            && _pcbPlacement.IpmLift != StationCylinderState.Up)
            return HomeBlockReason.PlacementHoldingPcb;
        if (requireRaised
            && (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
                || group is null && PcbHandlersEnabled)
            && (_pcbPlacement.Lift != StationCylinderState.Up
                || _pcbPlacement.IpmLift != StationCylinderState.Up))
            return HomeBlockReason.PlacementNotRaised;
        if (requireRaised
            && (group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
            && !_fasteningStation.IsHorizontalMoveAllowed)
            return HomeBlockReason.FasteningNotRaised;
        if (requireRaised
            && (group == MotionGroup.InspectionGantry || group is null && _units.Inspection)
            && !_inspectionStation.IsRaised)
            return HomeBlockReason.NgPickupNotRaised;
        return HomeBlockReason.None;
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
        return (live ? motion.Feedback.IsReady : motion.IsFeedbackAvailable)
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
                var homingAxes = false;
                using var operation = BeginManualOperation(
                    () => IsHomeAxisReady(group, axis, requireRaised: homingAxes),
                    cancellationToken);
                if (operation is null)
                    return;
                activeToken = operation.Token;
                if (_state.IsRunningFor(includeOperations: false))
                    return;
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
            catch (Exception exception) when (
                exception is not OperationCanceledException || !operation.IsCancellationRequested)
            {
                if (!_state.IsError)
                    _state.SetError(alarm, exception);
                else
                    _log?.LogError(exception,
                        "Cylinder raise {UnitAlarm} failed while stopping; existing alarm={Alarm}.",
                        alarm.ToString(), _state.Alarm.ToString());
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
        if (group == MotionGroup.InspectionGantry || group is null && _units.Inspection)
            await ObserveRaiseAsync(
                _inspectionStation.SetLiftUpAsync(true, operation.Token), MachineAlarm.NgCarrierTransfer);
        operation.Token.ThrowIfCancellationRequested();
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        await Task.Run(async () =>
        {
            var failureAlarm = MachineAlarm.MotionUnavailable;
            OperationCancellation.Operation? operation = null;
            try
            {
                operation = _operations.TryBegin(cancellationToken);
                if (operation is null)
                    return;
                cancellationToken = operation.Token;
                if (!IsHomeAllowedFor(_feedback.ReadLiveReadiness(), _state.IsRunningFor(includeOperations: false)))
                    return;
                cancellationToken.ThrowIfCancellationRequested();
                var homingAxes = false;
                var feedbackStartedAt = Stopwatch.GetTimestamp();
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

                        if (!_state.ServoMainContactorOn)
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

                void StopWhenHomeMotionBecomesUnavailable(MotionGroup group, MotionFeedbackSample sample)
                {
                    if (operation.IsCancellationRequested
                        || sample.StartedAt < feedbackStartedAt
                        || !sample.Enabled
                        || !_units.IsMotionEnabled(group))
                        return;
                    // Unhomed is expected during HOME; unavailable feedback, servo loss and faults are not.
                    if (sample.IoReady && sample.ReadError is null
                        && sample.Readiness.ServosOn && !sample.Readiness.Faulted)
                        return;
                    try
                    {
                        _state.SetError(_state.IsError ? _state.Alarm
                            : sample.IoReady ? MachineAlarm.MotionUnavailable : MachineAlarm.IoCommunication,
                            sample.ReadError ?? new InvalidOperationException(
                                $"Motion feedback {group} became unavailable during HOME: "
                                + $"servosOn={sample.Readiness.ServosOn}, faulted={sample.Readiness.Faulted}."));
                    }
                    finally
                    {
                        operation.Cancel();
                    }
                }

                _state.Changed += StopWhenHomeBecomesUnavailable;
                _feedback.Sampled += StopWhenHomeMotionBecomesUnavailable;
                try
                {
                    failureAlarm = MachineAlarm.HomeFailed;
                    _state.IsHoming = true;
                    await RaiseCylindersAsync(operation);
                    homingAxes = true;
                    StopWhenHomeBecomesUnavailable();
                    cancellationToken.ThrowIfCancellationRequested();
                    // Verify again after cylinder preparation, before issuing the first HOME.
                    failureAlarm = MachineAlarm.MotionUnavailable;
                    var motion = _feedback.ReadLiveReadiness();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (motion.Faulted || !motion.ServosOn)
                    {
                        _state.SetError(MachineAlarm.MotionUnavailable);
                        return;
                    }
                    failureAlarm = MachineAlarm.HomeFailed;
                    if (_units.PcbPlacement)
                    {
                        // Avoid interference: home Placement Z, Y, then X before any other unit starts.
                        await CheckHomeAsync(
                            _pcbPlacement.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        await CheckHomeAsync(
                            _pcbPlacement.HomeAxisAsync(MotionAxis.Y, cancellationToken), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        await CheckHomeAsync(
                            _pcbPlacement.HomeAxisAsync(MotionAxis.X, cancellationToken), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    await Task.WhenAll(
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
                        _units.PcbSupply
                            ? CheckHomeAsync(_pcbSupply.HomeHorizontalAsync(cancellationToken), cancellationToken)
                            : Task.CompletedTask,
                        _units.BoltFastening
                            ? CheckHomeAsync(_fasteningStation.HomeHorizontalAsync(cancellationToken), cancellationToken)
                            : Task.CompletedTask,
                        _units.Inspection
                            ? CheckHomeAsync(_inspectionStation.HomeHorizontalAsync(cancellationToken), cancellationToken)
                            : Task.CompletedTask);
                }
                finally
                {
                    _state.Changed -= StopWhenHomeBecomesUnavailable;
                    _feedback.Sampled -= StopWhenHomeMotionBecomesUnavailable;
                    _state.IsHoming = false;
                    _state.Refresh();
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _operations.IsShuttingDown)
            {
            }
            catch (Exception exception)
            {
                StopAndReportFailure(_state.IsError ? _state.Alarm : failureAlarm, exception);
            }
            finally
            {
                operation?.Dispose();
            }
        });
    }

    private async Task CheckHomeAsync(Task<bool> homing, CancellationToken cancellationToken)
    {
        try
        {
            if (!await homing && !cancellationToken.IsCancellationRequested)
                _state.SetError(MachineAlarm.HomeFailed);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
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
            try
            {
                if (!operation.IsCancellationRequested && !available())
                    operation.Cancel();
            }
            catch (Exception exception) when (IsDeviceFailure(exception))
            {
                // Reporting the alarm publishes another state change. Stop observing first.
                _state.Changed -= StopWhenUnavailable;
                StopAndReportFailure(_state.IsError ? _state.Alarm
                    : IsMotionFailure(exception) ? MachineAlarm.MotionUnavailable : MachineAlarm.IoCommunication,
                    exception);
            }
        }

        _state.Changed += StopWhenUnavailable;
        operation.Disposed += () => _state.Changed -= StopWhenUnavailable;
        try
        {
            // Admission failures belong to the command caller; later failures arrive on
            // the feedback publisher and must stop the operation without escaping there.
            if (!available())
                operation.Cancel();
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

    internal bool IsSetServoAllowed(MotionGroup group)
    {
        return _units.IsMotionEnabled(group)
            && _state.Available
            && _state.ManualMode
            && _state.SafetyReady
            && !_state.IsRunning
            && _state.GetMotionStatus(group).Axes.Values.All(axis => axis.State is not null);
    }

    internal void ToggleServo(MotionGroup group, MotionAxis axis)
    {
        try
        {
            using var operation = _operations.TryBegin();
            if (operation is null)
                return;
            if (!_units.IsMotionEnabled(group)
                || !_state.ManualMode
                || !_state.SafetyReady
                || _state.IsRunningFor(includeOperations: false)
                || !_state.GetMotionStatus(group).Feedback.IsReady)
                return;
            var on = !_state.GetMotionStatus(group).Feedback.GetAxisState(axis).ServoOn;
            if (operation.IsCancellationRequested)
                return;
            _motions[group].SetServo(axis, on);
        }
        catch (OperationCanceledException) when (_operations.IsShuttingDown)
        {
        }
        catch (Exception exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            _operations.Cancel();
        }
    }

    internal OperationCancellation.Operation BeginAdcProtocol(CancellationToken cancellationToken)
    {
        var operation = BeginManualOperation(() => AdcProtocolAvailable, cancellationToken);
        if (operation is null)
            throw new InvalidOperationException("Another machine operation acquired control before ADC diagnostics started.");
        try
        {
            if (!AdcProtocolAvailable || _state.IsRunningFor(includeOperations: false))
                throw new InvalidOperationException("ADC diagnostics require an idle machine in manual mode.");

            operation.Token.ThrowIfCancellationRequested();
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    internal void EnsureBoltTestAvailable()
    {
        if (!AdcProtocolAvailable)
        {
            throw new InvalidOperationException("Bolt testing requires safe manual mode.");
        }

    }

    public bool IsResetAllowed => IsResetAllowedFor(_state.IsRunning);

    private bool IsResetAllowedFor(bool running)
    {
        if (_operations.IsShuttingDown || _feedback.Failure is not null || running)
            return false;
        if (_state.Alarm == MachineAlarm.IoCommunication)
            return true;
        if (!_state.SafetyReady || !(_state.ManualMode || _state.DoorInterlockReady))
            return false;
        // Hardware recovery admission, not permission to acknowledge the buzzer.
        // A failed feedback scan must leave recovery usable without another native read.
        if (_state.IsError || _feedback.ReadError is not null)
            return true;
        var motion = _state.FeedbackReadiness;
        return motion.Faulted || !motion.ServosOn || !_state.ServoMainContactorOn;
    }

    public async Task ResetAsync()
    {
        try
        {
            await Task.Run(AcknowledgeAndResetAsync);
        }
        catch (OperationCanceledException exception) when (
            exception.CancellationToken.IsCancellationRequested || _operations.IsShuttingDown)
        {
            _log?.LogInformation("Machine RESET cancelled.");
        }
        catch (Exception exception)
        {
            // A disconnected controller cannot accept cleanup writes; keep its original failure.
            if (_io.IsReady)
                StopAndReportFailure(MachineAlarm.IoCommunication, exception);
            else
                _state.SetError(MachineAlarm.IoCommunication, exception);
        }
    }

    private Task AcknowledgeAndResetAsync()
    {
        SilenceBuzzer();
        lock (_resetGate)
        {
            // Repeated clicks acknowledge the buzzer, but share the current recovery.
            if (!_resetTask.IsCompleted)
                return _resetTask;
            return _resetTask = ResetHardwareAsync();
        }
    }

    private async Task ResetHardwareAsync()
    {
        var waitForCleanup = _state.IsError && _operations.HasActiveOperations
            && !_operations.IsShuttingDown && _feedback.Failure is null;
        if (waitForCleanup)
            _log?.LogInformation("Machine RESET: waiting for stopped operation cleanup to finish.");

        using var operation = waitForCleanup
            ? await _operations.TryBeginAfterIdleAsync()
            : _operations.TryBegin();
        if (operation is null)
            return;
        // Button availability uses acquired feedback; RESET must recheck the run outputs.
        // Disconnected I/O is initialized below without trying to read it first.
        if (!IsResetAllowedFor(_state.IsRunningFor(includeOperations: false)))
        {
            _log?.LogInformation("Machine RESET: hardware recovery conditions are not satisfied after the stop check.");
            return;
        }
        operation.Token.ThrowIfCancellationRequested();
        _log?.LogInformation("Machine RESET started.");
        await InitializeIoAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (!_state.SafetyReady || !_state.ManualMode && !_state.DoorInterlockReady)
            return;

        var alarm = MachineAlarm.None;
        var failures = new List<Exception>();
        void RecordFailure(MachineAlarm deviceAlarm, string device, Exception exception)
        {
            _log?.LogError(exception, "{Device} reset failed.", device);
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
            catch (Exception exception) when (
                exception is not OperationCanceledException || !operation.Token.IsCancellationRequested)
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
            catch (Exception exception) when (
                exception is not OperationCanceledException || !operation.Token.IsCancellationRequested)
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
            catch (Exception exception) when (
                exception is not OperationCanceledException || !operation.Token.IsCancellationRequested)
            {
                RecordFailure(MachineAlarm.BoltFastening, "Bolt controllers", exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        try
        {
            var motion = _feedback.ReadLiveReadiness();
            operation.Token.ThrowIfCancellationRequested();
            if (motion.Faulted || !motion.ServosOn)
                throw new InvalidOperationException(
                    $"Motion reset is not confirmed by hardware feedback: faulted={motion.Faulted}, servosOn={motion.ServosOn}.");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !operation.Token.IsCancellationRequested)
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
        UpdateMachineIndicators(silenceBuzzer: true);
    }

    // Called when alarm/run/motion readiness/NG state changes, not on every display refresh or sample.
    private void UpdateMachineIndicators(bool silenceBuzzer = false)
    {
        if (!_io.IsReady)
            return;

        // Idle axes can report a fault without a running sequence latching a machine alarm.
        var alarm = _state.Alarm;
        if (alarm == MachineAlarm.None && _state.FeedbackReadiness.Faulted)
            alarm = MachineAlarm.MotionUnavailable;
        _indicatorNotifications.Enqueue((
            alarm, _state.AutomaticRunning,
            _ngConveyor.AlarmRequired, _ngConveyor.IsEjectionPending, silenceBuzzer));
        do
        {
            if (Interlocked.CompareExchange(ref _indicatorWriterActive, 1, 0) != 0)
                return;
            try
            {
                while (_indicatorNotifications.TryDequeue(out var request))
                {
                    if (!_io.IsReady)
                        continue;
                    if (request.SilenceBuzzer)
                    {
                        _io.SetOutput(OutputIo.Buzzer, false);
                        continue;
                    }
                    var notification = (request.Alarm, request.Running, request.NgAlarm, request.NgEjectionPending);
                    var previous = _lastIndicatorNotification;
                    if (previous == notification)
                        continue;

                    var attention = notification.Alarm != MachineAlarm.None
                        || notification.NgAlarm || notification.NgEjectionPending;
                    var newAlarm = notification.Alarm != MachineAlarm.None
                            && notification.Alarm != previous?.Alarm
                        || notification.NgAlarm && !notification.NgEjectionPending
                            && (previous?.NgAlarm != true || previous?.NgEjectionPending == true);

                    _io.SetOutput(OutputIo.TowerLampGreen, notification.Running && !attention);
                    _io.SetOutput(OutputIo.TowerLampYellow, !notification.Running && !attention);
                    _io.SetOutput(OutputIo.TowerLampRed, attention);
                    // EJECT acknowledges the NG alert. Passing sensors during removal must
                    // not sound it again; independent machine faults still sound normally.
                    if (!attention || newAlarm
                        || notification.NgEjectionPending && notification.Alarm == MachineAlarm.None)
                        _io.SetOutput(OutputIo.Buzzer, newAlarm);

                    _lastIndicatorNotification = notification;
                }
            }
            catch (IOException exception)
            {
                _log?.LogError(exception, "Machine indicator output update failed.");
            }
            finally
            {
                Volatile.Write(ref _indicatorWriterActive, 0);
            }
        }
        while (!_indicatorNotifications.IsEmpty);
    }

    // OUTPUTS writes just the selected logical output. Feedback is display-only;
    // it does not start a conveyor sequence, move an axis, or wait for a cylinder.
    internal OutputBlockReason ToggleDiagnosticOutput(OutputIo signal)
    {
        try
        {
            if (_operations.IsShuttingDown)
                return OutputBlockReason.ShuttingDown;
            using var operation = _operations.Link();
            var block = ManualOutputSafetyBlock;
            if (block == OutputBlockReason.None && signal == OutputIo.PcbSupplyRotate && !_pcbSupply.IsRotationAllowed)
                block = OutputBlockReason.SupplyNotAtHandoff;
            if (block != OutputBlockReason.None)
            {
                _log?.LogInformation("Direct output {Signal} ignored: [{Block}] {Description}",
                    signal.ToString(), block.ToString(), block.GetDescription());
                return block;
            }

            var value = signal switch
            {
                OutputIo.MainConveyorNormalSpeed or OutputIo.NgConveyorNormalSpeed => true,
                OutputIo.PcbPlacementHandlerRotate => false,
                _ => !_io.GetOutput(signal),
            };
            operation.Token.ThrowIfCancellationRequested();
            _io.SetOutput(signal, value);
            _log?.LogInformation("Direct output {Signal}: {Value}; alarm={Alarm}.",
                signal.ToString(), value ? "ON" : "OFF", _state.Alarm.ToString());
            _state.Refresh();
            return OutputBlockReason.None;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
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
            if (_operations.IsShuttingDown)
                return OutputBlockReason.ShuttingDown;
            if (!_io.IsReady)
                return OutputBlockReason.IoUnavailable;
            if (!_state.ManualMode)
                return OutputBlockReason.AutoMode;
            if (!_state.EmergencyStopReleased)
                return OutputBlockReason.EmergencyStop;
            return OutputBlockReason.None;
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
                        _log?.LogInformation("Manual conveyor {Signal} {Action}: [{Reason}] {Description}",
                            signal.ToString(), outputStarted ? "stopped" : "ignored",
                            reason.ToString(), reason.GetDescription());
                    }
                }
                catch (Exception exception)
                {
                    // This callback runs on the I/O worker. Cancel without stopping its scan.
                    Interlocked.CompareExchange(ref failure, exception, null);
                    _log?.LogError(exception, "Manual conveyor {Signal}: interlock feedback could not be read.", signal.ToString());
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
                _log?.LogInformation("Manual conveyor {Signal}: ON, forward; alarm={Alarm}.", signal.ToString(), _state.Alarm.ToString());
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
                _log?.LogInformation("Manual conveyor {Signal}: OFF.", signal.ToString());
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
    internal static bool IsTeachingOutputSupported(OutputIo output)
    {
        return output is OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward
            or OutputIo.PcbSupplyRotate or OutputIo.PcbPlacementHandlerDown
            or OutputIo.PcbPlacementIpmDown or OutputIo.PcbPlacementVacuumEjector
            or OutputIo.PcbPlacementStopperUp or OutputIo.PcbPlacementBackupPlateUp
            or OutputIo.PickupHeadDown or OutputIo.PickupTableDown or OutputIo.ShootingHeadDown
            or OutputIo.PickupHeadVacuumPump or OutputIo.ShootingHeadVacuumPump or OutputIo.ShootBolt
            or OutputIo.BoltFasteningStopperUp or OutputIo.BoltFasteningBackupPlateUp
            or OutputIo.NgCarrierPickupDown or OutputIo.NgCarrierGripperClose or OutputIo.NgShuttleDown
            or OutputIo.InspectionStopperUp or OutputIo.InspectionBackupPlateUp;
    }

    internal bool IsSetTeachingOutputAllowed(IoOutputStatus output)
    {
        return IsTeachingOutputSupported(output.Signal)
            && _state.ManualSetupEnabled
            && (output.Signal != OutputIo.PcbSupplyRotate
                || IsManualMotionReady(MotionGroup.PcbSupply, live: false) && _pcbSupply.IsRotationAllowed);
    }

    internal async Task SetTeachingOutputAsync(
        IoOutputStatus output,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation = default,
        bool? requestedValue = null)
    {
        var activeToken = cancellationToken;
        try
        {
            if (!IsTeachingOutputSupported(output.Signal))
                return;
            using var operation = BeginManualOperation(
                () => _state.Available && _state.ManualMode && _state.SafetyReady
                    && (output.Signal != OutputIo.PcbSupplyRotate
                        || IsManualMotionReady(MotionGroup.PcbSupply) && _pcbSupply.IsRotationAllowed),
                cancellationToken,
                viewCancellation);
            if (operation is null)
                return;
            activeToken = operation.Token;
            if (_state.IsRunningFor(includeOperations: false))
                return;
            operation.Token.ThrowIfCancellationRequested();
            var value = requestedValue ?? !_io.GetOutput(output.Signal);
            operation.Token.ThrowIfCancellationRequested();
            switch (output.Signal)
            {
                case OutputIo.ShootBolt:
                    _io.SetOutput(output.Signal, value);
                    break;
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
                case OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward or OutputIo.PcbPlacementIpmDown
                    or OutputIo.PickupTableDown or OutputIo.PcbPlacementBackupPlateUp or OutputIo.BoltFasteningBackupPlateUp
                    or OutputIo.InspectionBackupPlateUp or OutputIo.PcbPlacementStopperUp or OutputIo.BoltFasteningStopperUp
                    or OutputIo.InspectionStopperUp:
                    await _io.SetOutputAndWaitAsync(output.Signal, value, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(output));
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewCancellation.IsCancellationRequested
            || _operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            ReportManualFailure(output.Area switch
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
