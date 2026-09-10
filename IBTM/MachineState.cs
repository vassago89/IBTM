using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Inspection.Training;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public enum MachineAlarm
{
    [Description("None")]
    None,

    [Description("Home Failed")]
    HomeFailed,

    [Description("Emergency Stop")]
    EmergencyStop,

    [Description("Door Open")]
    DoorOpen,

    [Description("Air Pressure Low")]
    AirPressureLow,

    [Description("Control I/O Communication")]
    IoCommunication,

    [Description("Motion Unavailable")]
    MotionUnavailable,

    [Description("PCB Supply")]
    PcbSupply,

    [Description("PCB Placement")]
    PcbPlacement,

    [Description("Pickup Bolt Feeder")]
    PickupBoltFeeder,

    [Description("Shooting Bolt Feeder")]
    ShootingBoltFeeder,

    [Description("Bolt Fastening")]
    BoltFastening,

    [Description("Inspection")]
    Inspection,

    [Description("NG Carrier Transfer")]
    NgCarrierTransfer,

    [Description("NG Shuttle")]
    NgShuttle,

    [Description("PCB Buffer Conflict")]
    BufferConflict,

    [Description("Main Conveyor")]
    MainConveyor,

    [Description("NG Conveyor")]
    NgConveyor,
}

public enum ManualControlBlock
{
    [Description("")]
    None,
    [Description("Reset the machine alarm.")]
    Alarm,
    [Description("Enable the servos and home the axes; clear motion alarms.")]
    MotionNotReady,
    [Description("Check emergency stops and air pressure.")]
    SafetyNotReady,
    [Description("Clear the PCB buffer conflict.")]
    BufferConflict,
    [Description("Switch the machine to Manual mode.")]
    AutoMode,
    [Description("Wait for the current operation to stop.")]
    Busy,
}

internal readonly record struct MotionReadiness(bool Homed, bool ServosOn, bool Faulted);

public sealed class MachineState : IDisposable, INotifyPropertyChanged
{
    private readonly AsyncAutoResetEvent _displayRequested = new();
    private readonly CancellationTokenSource _displayLifetime = new();
    private readonly TaskCompletionSource _firstDisplay = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _displayUpdates;
    private MachineDisplay _display = new();
    private readonly IReadOnlyDictionary<MotionGroup, MotionStatus> _motions;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly OperationCancellation _operations;
    private readonly IIoService _io;
    private readonly IoSignals _ioSignals;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly BufferStage _buffer;
    private readonly BoltTrainingSession _training;
    private readonly ApplicationLog? _log;

    public MachineState(
        MachineOptions options,
        UnitSettings units,
        OperationCancellation operations,
        IIoService io,
        IoSignals ioSignals,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        BufferStage buffer,
        BoltTrainingSession training,
        PcbSupplyHandler pcbSupply,
        PcbPlacementHandler pcbPlacement,
        BoltFasteningGantry boltFastening,
        InspectionGantry inspectionGantry,
        ApplicationLog? log = null)
    {
        _options = options;
        _units = units;
        _operations = operations;
        _io = io;
        _ioSignals = ioSignals;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _buffer = buffer;
        _training = training;
        _motions = new Dictionary<MotionGroup, MotionStatus>
        {
            [MotionGroup.PcbSupply] = pcbSupply.Motion,
            [MotionGroup.PcbPlacementHandler] = pcbPlacement.Motion,
            [MotionGroup.BoltFastening] = boltFastening.Motion,
            [MotionGroup.InspectionGantry] = inspectionGantry.Motion,
        };
        _log = log;

        io.InputChanged += (input, _) =>
        {
            // Feedback badges also use the shared UI refresh, not I/O-thread commands.
            RequestDisplayRefresh();
            if (AffectsMachineState(input))
            {
                NotifyChanged();
            }
        };
        io.OutputChanged += (output, _) =>
        {
            RequestDisplayRefresh();
            if (output is OutputIo.MainConveyorRun or OutputIo.NgConveyorRun)
            {
                NotifyChanged();
            }
        };
        buffer.PositionChanged += OnBufferPositionChanged;
        pcbSupply.Feedback.StateChanged += Refresh;
        pcbPlacement.Feedback.StateChanged += Refresh;
        boltFastening.Feedback.StateChanged += NotifyChanged;
        inspectionGantry.Feedback.StateChanged += NotifyChanged;
        conveyor.Changed += NotifyChanged;
        ngConveyor.Changed += NotifyChanged;
        training.Changed += NotifyChanged;
        operations.ActivityChanged += NotifyChanged;
    }

    public event Action? Changed;
    public event Action? DisplayChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public MachineDisplay Display
    {
        get
        {
            return Volatile.Read(ref _display);
        }

        private set
        {
            Volatile.Write(ref _display, value);
            PropertyChanged?.Invoke(this, new(nameof(Display)));
        }
    }

    public void RequestDisplayRefresh()
    {
        _displayRequested.Set();
    }

    internal MotionStatus GetMotionStatus(MotionGroup group)
    {
        return _motions.TryGetValue(group, out var motion)
            ? motion
            : throw new ArgumentOutOfRangeException(nameof(group));
    }

    internal async Task StartDisplayUpdatesAsync(Func<MachineDisplay> read)
    {
        if (_displayUpdates is null)
        {
            var monitors = new List<Task>();
            foreach (var (group, motion) in _motions)
                monitors.Add(
                    motion.StartMonitoringAsync(
                        _displayLifetime.Token,
                        RequestDisplayRefresh,
                        (
                            axis,
                            error) => _log?.Error(
                                $"Motion monitor {group}/{axis}: feedback read failed.",
                                error)));
            await Task.WhenAll(monitors).ConfigureAwait(false);
            Changed += RequestDisplayRefresh;
            RequestDisplayRefresh();
            _displayUpdates = Task.Run(() => UpdateDisplayLoopAsync(read));
        }

        await _firstDisplay.Task.ConfigureAwait(false);
    }

    private async Task UpdateDisplayLoopAsync(Func<MachineDisplay> read)
    {
        var cancellationToken = _displayLifetime.Token;
        try
        {
            while (true)
            {
                // Motion objects publish their own always-on monitor cache. Keep the
                // automatic watchdog for feedback providers without a monitor loop.
                await _displayRequested.WaitAsync(
                    AutomaticRunning ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan,
                    cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                RefreshDisplay(read);
                DisplayChanged?.Invoke();
                _firstDisplay.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _firstDisplay.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            _log?.Error("Display worker stopped by an unexpected error.", exception);
            Display = new() { ReadError = exception };
            _firstDisplay.TrySetException(exception);
            DisplayChanged?.Invoke();
            throw;
        }
        finally
        {
            Changed -= RequestDisplayRefresh;
        }
    }

    private void RefreshDisplay(Func<MachineDisplay> read)
    {
        try
        {
            _ioSignals.RefreshInputs();
            _ioSignals.RefreshOutputs();
            foreach (var (group, motion) in _motions)
                motion.RefreshControlFeedback(_io.IsReady && _units.IsMotionEnabled(group));
            Display = read();
        }
        catch (IOException exception)
        {
            if (Display.ReadError?.Message != exception.Message)
                _log?.Error("Display refresh failed.", exception);
            if (_io.IsReady)
            {
                Display = new() { ReadError = exception };
            }
            else
            {
                // The connection may fail partway through a scan.
                // Keep the original fault and invalidate every axis.
                _ioSignals.RefreshOutputs();
                foreach (var (_, motion) in _motions)
                    motion.RefreshControlFeedback(available: false);
                Display = read();
            }
        }
    }

    internal Task StopDisplayUpdatesAsync()
    {
        _displayLifetime.Cancel();
        var stopped = new List<Task> { _displayUpdates ?? Task.CompletedTask };
        foreach (var (_, motion) in _motions)
            stopped.Add(motion.MonitoringCompletion);
        return Task.WhenAll(stopped);
    }

    public void Dispose()
    {
        using (_displayLifetime)
            StopDisplayUpdatesAsync().GetAwaiter().GetResult();
    }

    internal MotionReadiness MotionReadiness
    {
        get
        {
            return ReadMotionReadiness(live: true);
        }
    }

    internal MotionReadiness DisplayMotionReadiness
    {
        get
        {
            return ReadMotionReadiness(live: false);
        }
    }

    private MotionReadiness ReadMotionReadiness(bool live)
    {
        var homed = true;
        var servosOn = true;
        var faulted = false;
        void Read(MotionStatus motion)
        {
            if (!motion.Feedback.IsReady)
            {
                homed = servosOn = false;
                faulted = true;
                return;
            }

            foreach (var axis in motion.Feedback.Axes)
            {
                var feedback = live ? motion.Feedback.GetAxisState(axis) : motion.Axes[axis].State;
                if (feedback is not { } state)
                {
                    homed = servosOn = false;
                    faulted = true;
                    continue;
                }

                homed &= state.Homed;
                servosOn &= state.ServoOn;
                faulted |= IsFaulted(state);
            }
        }

        foreach (var (group, motion) in _motions)
            if (_units.IsMotionEnabled(group))
                Read(motion);

        return new(homed, servosOn, faulted);
    }

    public bool Homed
    {
        get
        {
            return MotionReadiness.Homed;
        }
    }

    public bool ServosOn
    {
        get
        {
            return MotionReadiness.ServosOn;
        }
    }

    public bool Faulted
    {
        get
        {
            return MotionReadiness.Faulted;
        }
    }

    public bool Ready
    {
        get
        {
            return IsMotionReady(MotionReadiness);
        }
    }

    private bool IsMotionReady(MotionReadiness motion)
    {
        return ServoMainContactorOn && motion.Homed && motion.ServosOn && !motion.Faulted;
    }

    public bool EmergencyStopReleased
    {
        get
        {
            return _io.IsReady
                && !_io.GetInput(InputIo.EmergencyStop1Pressed)
                && !_io.GetInput(InputIo.EmergencyStop2Pressed);
        }
    }

    public bool DoorClosed
    {
        get
        {
            return
            // Door contacts are energized only while closed (legacy mapping keys end in Open).
            _io.IsReady
                && _io.GetInput(InputIo.Door1Open)
                && _io.GetInput(InputIo.Door2Open)
                && _io.GetInput(InputIo.Door3Open)
                && _io.GetInput(InputIo.Door4Open)
                && _io.GetInput(InputIo.Door5Open)
                && _io.GetInput(InputIo.Door6Open);
        }
    }

    public bool AirPressureOk
    {
        get
        {
            return _io.IsReady && _io.GetInput(InputIo.AirPressureHigh);
        }
    }

    public bool ServoMainContactorOn
    {
        get
        {
            return _io.IsReady && _io.GetInput(InputIo.ServoMainContactorOn);
        }
    }

    public bool AutoMode
    {
        get
        {
            return
            // The selector contact is energized in MANUAL, open in AUTO.
            _io.IsReady && !_io.GetInput(InputIo.AutoMode);
        }
    }

    public bool ManualMode
    {
        get
        {
            return !AutoMode;
        }
    }

    public bool DoorInterlockReady
    {
        get
        {
            return !_options.UseDoorInterlock || DoorClosed;
        }
    }

    public bool SafetyReady
    {
        get
        {
            return (!_options.UseEmergencyStop || EmergencyStopReleased)
                && (!_options.UseAirPressureInterlock || AirPressureOk);
        }
    }

    public bool IsError
    {
        get
        {
            return Alarm != MachineAlarm.None;
        }
    }

    public bool AutomaticRunning { get; private set; }
    public bool BoltTestRunning { get; private set; }
    public bool IsHoming { get; private set; }
    public MachineAlarm Alarm { get; private set; }
    public string? AlarmDetail { get; private set; }
    public string? AlarmMessage { get; private set; }

    public bool ConveyorRunning
    {
        get
        {
            return _io.IsReady && _conveyor.RunCommandOn;
        }
    }

    public MainConveyorState MainConveyorState
    {
        get
        {
            return _conveyor.State;
        }
    }

    public bool SupplyInBufferArea
    {
        get
        {
            return _buffer.SupplyInside;
        }
    }

    internal bool PlacementInBufferArea
    {
        get
        {
            return _buffer.PlacementInside;
        }
    }

    internal bool SupplyAtHandoff
    {
        get
        {
            return _buffer.SupplyAtHandoff;
        }
    }

    internal bool CanSupplyEnter
    {
        get
        {
            return _buffer.CanSupplyEnter;
        }
    }

    public bool BufferConflict
    {
        get
        {
            return _buffer.Conflict;
        }
    }

    public bool IsRunning
    {
        get
        {
            return _training.IsRunning
                || _operations.HasActiveOperations
                || AutomaticRunning
                || BoltTestRunning
                || IsHoming
                || ConveyorRunning // A motion already in progress must still keep the machine busy, even if
            // its unit is disabled programmatically before the operation has stopped.
                || _motions.Values.Any(static motion => motion.Feedback.IsMoving)
                || (_io.IsReady && _ngConveyor.RunCommandOn);
        }
    }

    public bool CanOperate
    {
        get
        {
            return !IsError && Ready && SafetyReady && !BufferConflict;
        }
    }

    public bool CanAutomaticOperate
    {
        get
        {
            return CanOperate && AutoMode && DoorInterlockReady;
        }
    }

    public ManualControlBlock ManualBlock
    {
        get
        {
            return GetManualBlock(MotionReadiness);
        }
    }

    internal ManualControlBlock GetManualBlock(MotionReadiness motion)
    {
        return this switch
        {
            { IsError: true } => ManualControlBlock.Alarm,
            _ when !IsMotionReady(motion) => ManualControlBlock.MotionNotReady,
            { SafetyReady: false } => ManualControlBlock.SafetyNotReady,
            { BufferConflict: true } => ManualControlBlock.BufferConflict,
            { AutoMode: true } => ManualControlBlock.AutoMode,
            { IsRunning: true } => ManualControlBlock.Busy,
            _ => ManualControlBlock.None,
        };
    }

    public bool ManualControlsEnabled
    {
        get
        {
            return ManualBlock == ManualControlBlock.None;
        }
    }

    // Editing data does not operate a device or require motion readiness.
    public bool SetupEditingEnabled
    {
        get
        {
            return !_operations.IsShuttingDown && ManualMode && !IsRunning;
        }
    }

    // Teaching hardware commands and coordinated cylinder preparation. OUTPUTS uses
    // MachineController.ToggleDiagnosticOutput and does not require teaching readiness.
    public bool ManualSetupEnabled
    {
        get
        {
            return _io.IsReady
                && !_operations.IsShuttingDown
                && ManualMode
                && SafetyReady
                && !IsRunning;
        }
    }

    public void Refresh()
    {
        if (Alarm == MachineAlarm.None && BufferConflict)
        {
            Alarm = MachineAlarm.BufferConflict;
        }

        NotifyChanged();
    }

    internal void SetHoming(bool value)
    {
        if (IsHoming != value)
            _log?.Write($"Homing {(value ? "started" : "finished")}.");
        IsHoming = value;
        Changed?.Invoke();
    }

    internal void SetAutomaticRunning(bool value)
    {
        if (AutomaticRunning != value)
            _log?.Write($"Automatic operation {(value ? "started" : "stopped")}.");
        AutomaticRunning = value;
        Changed?.Invoke();
    }

    internal void SetBoltTestRunning(bool value)
    {
        if (BoltTestRunning != value)
            _log?.Write($"Bolt test {(value ? "started" : "stopped")}.");
        BoltTestRunning = value;
        Changed?.Invoke();
    }

    internal void SetError(MachineAlarm alarm, Exception? exception = null)
    {
        if (Alarm == alarm && (exception is null || AlarmDetail is not null))
        {
            return;
        }

        Alarm = alarm;
        AlarmDetail = exception?.ToString();
        AlarmMessage = exception?.Message;
        _log?.Error($"Machine alarm: {alarm}.", exception);
        Changed?.Invoke();
    }

    internal void ClearError()
    {
        if (Alarm != MachineAlarm.None)
            _log?.Write($"Machine alarm cleared: {Alarm}.");
        Alarm = MachineAlarm.None;
        AlarmDetail = null;
        AlarmMessage = null;
        Changed?.Invoke();
    }

    private void OnBufferPositionChanged()
    {
        if (Alarm == MachineAlarm.None && BufferConflict)
        {
            Alarm = MachineAlarm.BufferConflict;
            NotifyChanged();
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    internal static bool IsSafetyInput(InputIo input)
    {
        return input is InputIo.EmergencyStop1Pressed
            or InputIo.EmergencyStop2Pressed
            or InputIo.AutoMode
            or InputIo.Door1Open
            or InputIo.Door2Open
            or InputIo.Door3Open
            or InputIo.Door4Open
            or InputIo.Door5Open
            or InputIo.Door6Open
            or InputIo.AirPressureHigh;
    }

    private static bool AffectsMachineState(InputIo input)
    {
        return input == InputIo.ServoMainContactorOn || IsSafetyInput(input);
    }

    private static bool IsFaulted(AxisState state)
    {
        return state.Alarm || state.Emergency;
    }
}
