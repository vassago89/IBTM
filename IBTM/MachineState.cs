using System;
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

    [Description("PCB Handoff Conflict")]
    BufferConflict,

    [Description("Main Conveyor")]
    MainConveyor,

    [Description("NG Conveyor")]
    NgConveyor,

    [Description("Stop Failed — Check Equipment")]
    StopFailed,
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
    [Description("Clear the PCB Handoff conflict.")]
    BufferConflict,
    [Description("Switch the machine to Manual mode.")]
    AutoMode,
    [Description("Wait for the current operation to stop.")]
    Busy,
}

public sealed class MachineState : IDisposable, INotifyPropertyChanged
{
    private readonly AsyncAutoResetEvent _displayRequested = new();
    private readonly CancellationTokenSource _displayLifetime = new();
    private readonly TaskCompletionSource _firstDisplay = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _displayUpdates;
    private MachineDisplay _display = new();
    private bool _repeatEnabled;
    // Last handled notification, not the physical state of the lamp/buzzer outputs.
    private (MachineAlarm Alarm, bool Running, bool NgAlarm)? _lastIndicatorNotification;
    private readonly MachineFeedbackMonitor _feedback;
    private readonly MachineOptions _options;
    private readonly OperationCancellation _operations;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    internal BufferStage Buffer { get; }
    private readonly ApplicationLog? _log;

    public bool RepeatEnabled
    {
        get
        {
            return _repeatEnabled;
        }
        set
        {
            if (_repeatEnabled == value || !SetupEditingEnabled)
                return;
            _repeatEnabled = value;
            PropertyChanged?.Invoke(this, new(nameof(RepeatEnabled)));
            RequestDisplayRefresh();
        }
    }

    public MachineState(
        MachineOptions options,
        OperationCancellation operations,
        IIoService io,
        MachineFeedbackMonitor feedback,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        BufferStage buffer,
        PcbSupplyHandler pcbSupply,
        PcbPlacementHandler pcbPlacement,
        BoltFasteningGantry boltFastening,
        InspectionGantry inspectionGantry,
        ApplicationLog? log = null)
    {
        _options = options;
        _operations = operations;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        Buffer = buffer;
        _feedback = feedback;
        _log = log;

        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
        buffer.PositionChanged += OnBufferPositionChanged;
        pcbSupply.Feedback.StateChanged += OnBufferMotionStateChanged;
        pcbPlacement.Feedback.StateChanged += OnBufferMotionStateChanged;
        boltFastening.Feedback.StateChanged += OnMotionStateChanged;
        inspectionGantry.Feedback.StateChanged += OnMotionStateChanged;
        conveyor.Changed += NotifyChanged;
        ngConveyor.Changed += OnNgConveyorChanged;
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
        return _feedback.Motions.TryGetValue(group, out var motion)
            ? motion
            : throw new ArgumentOutOfRangeException(nameof(group));
    }

    internal async Task StartDisplayUpdatesAsync(Func<MachineDisplay> read)
    {
        if (_displayUpdates is null)
        {
            _feedback.Changed += RequestDisplayRefresh;
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
                await _displayRequested.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _feedback.Changed -= RequestDisplayRefresh;
        }
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
            _log?.Error("Buzzer OFF failed.", exception);
        }
    }

    // Called by alarm/run/NG notifications, never by the display or acquisition loops.
    internal void UpdateMachineIndicators()
    {
        if (!_io.IsReady)
            return;

        try
        {
            var notification = (Alarm, Running: AutomaticRunning, NgAlarm: _ngConveyor.AlarmRequired);
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
            _log?.Error("Machine indicator output update failed.", exception);
        }
    }

    private void RefreshDisplay(Func<MachineDisplay> read)
    {
        try
        {
            if (_feedback.ReadError is { } error)
            {
                if (Display.ReadError?.Message != error.Message)
                    Display = new() { ReadError = error };
                return;
            }

            Display = read();
        }
        catch (IOException exception)
        {
            if (Display.ReadError?.Message != exception.Message)
                _log?.Error("Display refresh failed.", exception);
            if (_io.IsReady)
            {
                if (Display.ReadError?.Message != exception.Message)
                    Display = new() { ReadError = exception };
            }
            else
            {
                // The connection may fail partway through a scan.
                // Keep the original fault; the feedback monitor invalidates control axes.
                Display = read();
            }
        }
    }

    internal Task StopDisplayUpdatesAsync()
    {
        _displayLifetime.Cancel();
        return _displayUpdates ?? Task.CompletedTask;
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
            return _feedback.ReadLiveReadiness();
        }
    }

    internal MotionReadiness FeedbackReadiness
    {
        get
        {
            return _feedback.Readiness;
        }
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
            return !_options.UseDoorInterlock || ManualMode || DoorClosed;
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

    public bool IsRunning
    {
        get
        {
            return GetIsRunning();
        }
    }

    internal bool GetIsRunning(bool? mainRunning = null, bool? ngRunning = null)
    {
        return _operations.HasActiveOperations
            || AutomaticRunning
            || BoltTestRunning
            || IsHoming
            || (_io.IsReady && (mainRunning ?? _conveyor.RunCommandOn))
            // Always-on observations include disabled axes, without issuing native
            // calls while the UI evaluates commands such as RESET.
            || _feedback.Motions.Values.Any(static motion => motion.IsMoving)
            || (_io.IsReady && (ngRunning ?? _ngConveyor.RunCommandOn));
    }

    public bool CanOperate
    {
        get
        {
            return !IsError && Ready && SafetyReady && !Buffer.HasConflict();
        }
    }

    public ManualControlBlock ManualBlock
    {
        get
        {
            return GetManualBlock(MotionReadiness);
        }
    }

    internal ManualControlBlock GetManualBlock(
        MotionReadiness motion,
        bool? bufferConflict = null,
        bool? running = null)
    {
        return this switch
        {
            { IsError: true } => ManualControlBlock.Alarm,
            _ when !IsMotionReady(motion) => ManualControlBlock.MotionNotReady,
            { SafetyReady: false } => ManualControlBlock.SafetyNotReady,
            _ when bufferConflict ?? Buffer.HasConflict() => ManualControlBlock.BufferConflict,
            { AutoMode: true } => ManualControlBlock.AutoMode,
            _ when running ?? IsRunning => ManualControlBlock.Busy,
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
            return !_operations.IsShuttingDown
                && ManualMode
                && !_operations.HasActiveOperations
                && !AutomaticRunning
                && !BoltTestRunning
                && !IsHoming;
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
        OnBufferMotionStateChanged();
        RequestDisplayRefresh();
    }

    private void OnBufferMotionStateChanged()
    {
        if (Alarm == MachineAlarm.None && Buffer.HasConflict())
        {
            SetError(MachineAlarm.BufferConflict);
            return;
        }

        Changed?.Invoke();
    }

    private void OnMotionStateChanged()
    {
        Changed?.Invoke();
    }

    internal void SetHoming(bool value)
    {
        if (IsHoming != value)
            _log?.Write($"Homing {(value ? "started" : "finished")}.");
        IsHoming = value;
        NotifyChanged();
    }

    internal void SetAutomaticRunning(bool value)
    {
        if (AutomaticRunning == value)
            return;

        _log?.Write($"Automatic operation {(value ? "started" : "stopped")}.");
        AutomaticRunning = value;
        UpdateMachineIndicators();
        NotifyChanged();
    }

    internal void SetBoltTestRunning(bool value)
    {
        if (BoltTestRunning != value)
            _log?.Write($"Bolt test {(value ? "started" : "stopped")}.");
        BoltTestRunning = value;
        NotifyChanged();
    }

    internal void SetError(MachineAlarm alarm, Exception? exception = null)
    {
        if (Alarm == alarm && (exception is null || AlarmDetail is not null))
        {
            if (exception is not null)
            {
                _log?.Error($"Machine alarm remains: {alarm}.", exception);
            }

            return;
        }

        Alarm = alarm;
        AlarmDetail = exception?.ToString();
        AlarmMessage = exception?.Message;
        _log?.Error($"Machine alarm: {alarm}.", exception);
        UpdateMachineIndicators();
        NotifyChanged();
    }

    internal void ClearError()
    {
        if (Alarm != MachineAlarm.None)
            _log?.Write($"Machine alarm cleared: {Alarm}.");
        Alarm = MachineAlarm.None;
        AlarmDetail = null;
        AlarmMessage = null;
        UpdateMachineIndicators();
        NotifyChanged();
    }

    private void OnBufferPositionChanged()
    {
        if (Alarm == MachineAlarm.None && Buffer.HasConflict())
        {
            SetError(MachineAlarm.BufferConflict);
        }
    }

    private void OnNgConveyorChanged()
    {
        UpdateMachineIndicators();
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
        RequestDisplayRefresh();
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

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.ServoMainContactorOn || IsSafetyInput(input))
            Changed?.Invoke();
        RequestDisplayRefresh();
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output is OutputIo.MainConveyorRun or OutputIo.NgConveyorRun)
            Changed?.Invoke();
    }
}
