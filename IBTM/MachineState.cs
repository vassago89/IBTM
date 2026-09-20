using System.ComponentModel;
using System.Linq;
using System;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

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
    [Description("Switch the machine to Manual mode.")]
    AutoMode,
    [Description("Wait for the current operation to stop.")]
    Busy,
}

public sealed class MachineState : INotifyPropertyChanged
{
    private readonly MachineFeedbackMonitor _feedback;
    private readonly MachineOptions _options;
    private readonly OperationCancellation _operations;
    private readonly IIoService _io;
    private readonly ILogger<MachineState>? _log;

    public MachineState(
        MachineOptions options,
        OperationCancellation operations,
        IIoService io,
        MachineFeedbackMonitor feedback,
        ILogger<MachineState>? log = null)
    {
        _options = options;
        _operations = operations;
        _io = io;
        _feedback = feedback;
        _log = log;

        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
        foreach (var motion in feedback.Motions.Values)
            motion.Feedback.StateChanged += OnMotionStateChanged;
        operations.ActivityChanged += NotifyChanged;
        feedback.Changed += OnFeedbackChanged;
        feedback.Io.PropertyChanged += OnFeedbackPropertyChanged;
        feedback.Io.Outputs[OutputIo.MainConveyorRun].PropertyChanged += OnFeedbackPropertyChanged;
        feedback.Io.Outputs[OutputIo.NgConveyorRun].PropertyChanged += OnFeedbackPropertyChanged;
        foreach (var motion in feedback.Motions.Values)
        {
            motion.PropertyChanged += OnMotionPropertyChanged;
            foreach (var axis in motion.Axes.Values)
                axis.PropertyChanged += OnFeedbackPropertyChanged;
        }
    }

    public event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool RepeatEnabled
    {
        get;
        set
        {
            if (field == value || !SetupEditingEnabled)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(RepeatEnabled)));
            OnFeedbackChanged();
        }
    }

    public bool Available => _io.IsReady && ReadError is null
        && _feedback.Io.Outputs[OutputIo.MainConveyorRun].IsOn is not null
        && _feedback.Io.Outputs[OutputIo.NgConveyorRun].IsOn is not null;

    public Exception? ReadError => _feedback.ReadError;

    public bool ServoPowerOn => ServoMainContactorOn && ServosOn;

    internal MotionReadiness MotionReadiness => _feedback.ReadLiveReadiness();

    internal MotionReadiness FeedbackReadiness => _feedback.Readiness;

    public bool Homed => FeedbackReadiness.Homed;

    public bool ServosOn => FeedbackReadiness.ServosOn;

    public bool Faulted => FeedbackReadiness.Faulted;

    public bool Ready => Available && IsMotionReady(FeedbackReadiness);

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

    public bool AirPressureOk => _io.IsReady && _io.GetInput(InputIo.AirPressureHigh);

    public bool ServoMainContactorOn => _io.IsReady && _io.GetInput(InputIo.ServoMainContactorOn);

    // The selector contact is energized in MANUAL, open in AUTO.
    public bool AutoMode => _io.IsReady && !_io.GetInput(InputIo.AutoMode);

    public bool ManualMode => !AutoMode;

    public bool DoorInterlockReady => !_options.UseDoorInterlock || ManualMode || DoorClosed;

    public bool SafetyReady
    {
        get
        {
            return (!_options.UseEmergencyStop || EmergencyStopReleased)
                && (!_options.UseAirPressureInterlock || AirPressureOk);
        }
    }

    public bool IsError => Alarm != MachineAlarm.None;

    public bool AutomaticRunning
    {
        get;
        internal set
        {
            if (field == value)
                return;

            _log?.LogInformation("{Message}", $"Automatic operation {(value ? "started" : "stopped")}.");
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(AutomaticRunning)));
            NotifyChanged();
        }
    }

    public bool BoltTestRunning
    {
        get;
        internal set
        {
            if (field == value)
                return;
            _log?.LogInformation("{Message}", $"Bolt test {(value ? "started" : "stopped")}.");
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(BoltTestRunning)));
            NotifyChanged();
        }
    }

    public bool IsHoming
    {
        get;
        internal set
        {
            if (field == value)
                return;
            _log?.LogInformation("{Message}", $"Homing {(value ? "started" : "finished")}.");
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(IsHoming)));
            NotifyChanged();
        }
    }

    public MachineAlarm Alarm { get; private set; }
    public string? AlarmDetail { get; private set; }
    public string? AlarmMessage { get; private set; }

    public bool IsRunning => IsRunningFor(
        _feedback.Io.Outputs[OutputIo.MainConveyorRun].IsOn == true,
        _feedback.Io.Outputs[OutputIo.NgConveyorRun].IsOn == true);

    public ManualControlBlock ManualBlock => GetManualBlock(FeedbackReadiness, IsRunning);

    public bool ManualControlsEnabled => Available && ManualBlock == ManualControlBlock.None;

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
            return Available
                && !_operations.IsShuttingDown
                && ManualMode
                && SafetyReady
                && !IsRunning;
        }
    }

    internal MotionStatus GetMotionStatus(MotionGroup group)
    {
        return _feedback.Motions.TryGetValue(group, out var motion)
            ? motion
            : throw new ArgumentOutOfRangeException(nameof(group));
    }

    private bool IsMotionReady(MotionReadiness motion)
    {
        return ServoMainContactorOn && motion.Homed && motion.ServosOn && !motion.Faulted;
    }

    internal bool IsRunningFor(bool? mainRunning = null, bool? ngRunning = null)
    {
        return _operations.HasActiveOperations
            || AutomaticRunning
            || BoltTestRunning
            || IsHoming
            || (_io.IsReady && (mainRunning ?? _io.GetOutput(OutputIo.MainConveyorRun)))
            // Always-on observations include disabled axes, without issuing native
            // calls while the UI evaluates commands such as RESET.
            || _feedback.Motions.Values.Any(static motion => motion.IsMoving)
            || (_io.IsReady && (ngRunning ?? _io.GetOutput(OutputIo.NgConveyorRun)));
    }

    internal ManualControlBlock GetManualBlock(
        MotionReadiness motion,
        bool? running = null)
    {
        switch (this)
        {
            case { IsError: true }:
                return ManualControlBlock.Alarm;
            case var _ when !IsMotionReady(motion):
                return ManualControlBlock.MotionNotReady;
            case { SafetyReady: false }:
                return ManualControlBlock.SafetyNotReady;
            case { AutoMode: true }:
                return ManualControlBlock.AutoMode;
            case var _ when running ?? IsRunning:
                return ManualControlBlock.Busy;
            default:
                return ManualControlBlock.None;
        }
    }

    public void Refresh()
    {
        Changed?.Invoke();
        PropertyChanged?.Invoke(this, new(null));
    }

    private void OnFeedbackChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(Available)));
        PropertyChanged?.Invoke(this, new(nameof(ReadError)));
        PropertyChanged?.Invoke(this, new(nameof(Homed)));
        PropertyChanged?.Invoke(this, new(nameof(ServosOn)));
        PropertyChanged?.Invoke(this, new(nameof(Faulted)));
        PropertyChanged?.Invoke(this, new(nameof(ServoPowerOn)));
        PropertyChanged?.Invoke(this, new(nameof(IsRunning)));
        PropertyChanged?.Invoke(this, new(nameof(ManualBlock)));
        PropertyChanged?.Invoke(this, new(nameof(ManualControlsEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(ManualSetupEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(SetupEditingEnabled)));
    }

    private void OnFeedbackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AxisStatus && e.PropertyName != nameof(AxisStatus.State)
            || sender is IoOutputStatus && e.PropertyName != nameof(IoOutputStatus.IsOn))
            return;
        OnFeedbackChanged();
    }

    private void OnMotionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MotionStatus.IsMoving))
            OnFeedbackChanged();
    }

    private void OnMotionStateChanged()
    {
        Changed?.Invoke();
    }

    internal void SetError(MachineAlarm alarm, Exception? exception = null)
    {
        if (Alarm == alarm && (exception is null || AlarmDetail is not null))
        {
            if (exception is not null)
            {
                _log?.LogError(exception, "{Message}", $"Machine alarm remains: {alarm}.");
            }

            return;
        }

        Alarm = alarm;
        AlarmDetail = exception?.ToString();
        AlarmMessage = exception?.Message;
        _log?.LogError(exception, "{Message}", $"Machine alarm: {alarm}.");
        PropertyChanged?.Invoke(this, new(nameof(Alarm)));
        PropertyChanged?.Invoke(this, new(nameof(AlarmDetail)));
        PropertyChanged?.Invoke(this, new(nameof(AlarmMessage)));
        NotifyChanged();
    }

    internal void ClearError()
    {
        if (Alarm != MachineAlarm.None)
            _log?.LogInformation("{Message}", $"Machine alarm cleared: {Alarm}.");
        Alarm = MachineAlarm.None;
        AlarmDetail = null;
        AlarmMessage = null;
        PropertyChanged?.Invoke(this, new(nameof(Alarm)));
        PropertyChanged?.Invoke(this, new(nameof(AlarmDetail)));
        PropertyChanged?.Invoke(this, new(nameof(AlarmMessage)));
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
        OnFeedbackChanged();
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
        {
            Changed?.Invoke();
            OnFeedbackChanged();
            PropertyChanged?.Invoke(this, new(nameof(SafetyReady)));
            PropertyChanged?.Invoke(this, new(nameof(EmergencyStopReleased)));
            PropertyChanged?.Invoke(this, new(nameof(DoorClosed)));
            PropertyChanged?.Invoke(this, new(nameof(AirPressureOk)));
            PropertyChanged?.Invoke(this, new(nameof(AutoMode)));
        }
        PropertyChanged?.Invoke(this, new(nameof(ManualSetupEnabled)));
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output is OutputIo.MainConveyorRun or OutputIo.NgConveyorRun)
            Changed?.Invoke();
    }
}
