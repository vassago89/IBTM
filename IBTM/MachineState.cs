using System;
using System.ComponentModel;
using System.IO;
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

internal readonly record struct MotionReadiness(
    bool Homed,
    bool ServosOn,
    bool Faulted);

public sealed class MachineState : IDisposable
{
    private readonly AsyncAutoResetEvent _displayRequested = new();
    private readonly CancellationTokenSource _displayLifetime = new();
    private readonly TaskCompletionSource _firstDisplay = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _displayUpdates;
    private MachineDisplay _display = new();
    private readonly MotionStatus[] _motionDisplays;
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly OperationCancellation _operations;
    private readonly IIoService _io;
    private readonly IoSignals _ioSignals;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly BufferStage _buffer;
    private readonly BoltTrainingSession _training;
    private readonly MotionStatus _supplyMotion;
    private readonly MotionStatus _placementMotion;
    private readonly MotionStatus _fasteningMotion;
    private readonly MotionStatus _inspectionMotion;

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
        InspectionGantry inspectionGantry)
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
        _motionDisplays = [pcbSupply.Motion, pcbPlacement.Motion, boltFastening.Motion, inspectionGantry.Motion];
        _supplyMotion = pcbSupply.Motion;
        _placementMotion = pcbPlacement.Motion;
        _fasteningMotion = boltFastening.Motion;
        _inspectionMotion = inspectionGantry.Motion;

        io.InputChanged += (input, _) =>
        {
            if (AffectsMachineState(input))
            {
                NotifyChanged();
            }
        };
        io.OutputChanged += (output, _) =>
        {
            RequestDisplayRefresh();
            if (output is OutputIo.MainConveyorRun
                or OutputIo.NgConveyorRun)
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
    public MachineDisplay Display
    {
        get => Volatile.Read(ref _display);
        private set => Volatile.Write(ref _display, value);
    }

    public void RequestDisplayRefresh() => _displayRequested.Set();

    internal Task StartDisplayUpdatesAsync(Func<MachineDisplay> read)
    {
        if (_displayUpdates is null)
        {
            Changed += RequestDisplayRefresh;
            RequestDisplayRefresh();
            _displayUpdates = Task.Run(async () =>
            {
                var cancellationToken = _displayLifetime.Token;
                try
                {
                    while (true)
                    {
                        await _displayRequested.WaitAsync(cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            _ioSignals.RefreshOutputs();
                            foreach (var motion in _motionDisplays) motion.RefreshAxes();
                            Display = read();
                        }
                        catch (IOException exception)
                        {
                            Display = new() { ReadError = exception };
                        }
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
                    Display = new() { ReadError = exception };
                    _firstDisplay.TrySetException(exception);
                    DisplayChanged?.Invoke();
                    throw;
                }
                finally
                {
                    Changed -= RequestDisplayRefresh;
                }
            });
        }
        return _firstDisplay.Task;
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

    internal MotionReadiness MotionReadiness => ReadMotionReadiness(live: true);
    internal MotionReadiness DisplayMotionReadiness => ReadMotionReadiness(live: false);

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

        if (_units.PcbSupply || _units.PcbPlacement)
        {
            Read(_supplyMotion);
            Read(_placementMotion);
        }

        if (_units.BoltFastening) Read(_fasteningMotion);
        if (_units.Inspection || _units.NgCarrierTransfer) Read(_inspectionMotion);

        return new(homed, servosOn, faulted);
    }

    public bool Homed => MotionReadiness.Homed;
    public bool ServosOn => MotionReadiness.ServosOn;
    public bool Faulted => MotionReadiness.Faulted;
    public bool Ready => IsMotionReady(MotionReadiness);
    private bool IsMotionReady(MotionReadiness motion) =>
        ServoMainContactorOn && motion.Homed && motion.ServosOn && !motion.Faulted;

    public bool EmergencyStopReleased =>
        _io.IsReady
        && !_io.GetInput(InputIo.EmergencyStop1Pressed)
        && !_io.GetInput(InputIo.EmergencyStop2Pressed);
    public bool DoorClosed =>
        _io.IsReady
        && !_io.GetInput(InputIo.Door1Open)
        && !_io.GetInput(InputIo.Door2Open)
        && !_io.GetInput(InputIo.Door3Open)
        && !_io.GetInput(InputIo.Door4Open)
        && !_io.GetInput(InputIo.Door5Open)
        && !_io.GetInput(InputIo.Door6Open);
    public bool AirPressureOk =>
        _io.IsReady
        && !_io.GetInput(InputIo.AirPressureLow);
    public bool ServoMainContactorOn =>
        _io.IsReady && _io.GetInput(InputIo.ServoMainContactorOn);
    public bool AutoMode =>
        _io.IsReady && _io.GetInput(InputIo.AutoMode);
    public bool ManualMode => !AutoMode;
    public bool DoorInterlockReady =>
        !_options.UseDoorInterlock || DoorClosed;
    public bool SafetyReady =>
        (!_options.UseEmergencyStop || EmergencyStopReleased)
        && (!_options.UseAirPressureInterlock || AirPressureOk);

    public bool IsError => Alarm != MachineAlarm.None;
    public bool AutomaticRunning { get; private set; }
    public bool BoltTestRunning { get; private set; }
    public bool IsHoming { get; private set; }
    public MachineAlarm Alarm { get; private set; }
    public string? AlarmDetail { get; private set; }
    public string? AlarmMessage { get; private set; }

    public bool ConveyorRunning => _conveyor.RunCommandOn;
    public MainConveyorState MainConveyorState => _conveyor.State;
    public bool SupplyInBufferArea => _buffer.SupplyInside;
    internal bool PlacementInBufferArea => _buffer.PlacementInside;
    internal bool SupplyAtHandoff => _buffer.SupplyAtHandoff;
    internal bool CanSupplyEnter => _buffer.CanSupplyEnter;
    public bool BufferConflict => _buffer.Conflict;

    public bool IsRunning =>
        _training.IsRunning
        || _operations.HasActiveOperations
        || AutomaticRunning
        || BoltTestRunning
        || IsHoming
        || ConveyorRunning
        || Array.Exists(_motionDisplays, static motion => motion.Feedback.IsMoving)
        || _ngConveyor.RunCommandOn;

    public bool CanOperate =>
        !IsError
        && Ready
        && SafetyReady
        && !BufferConflict;
    public bool CanAutomaticOperate =>
        CanOperate
        && AutoMode
        && DoorInterlockReady;
    public ManualControlBlock ManualBlock => GetManualBlock(MotionReadiness);
    internal ManualControlBlock GetManualBlock(MotionReadiness motion) => this switch
    {
        { IsError: true } => ManualControlBlock.Alarm,
        _ when !IsMotionReady(motion) => ManualControlBlock.MotionNotReady,
        { SafetyReady: false } => ManualControlBlock.SafetyNotReady,
        { BufferConflict: true } => ManualControlBlock.BufferConflict,
        { AutoMode: true } => ManualControlBlock.AutoMode,
        { IsRunning: true } => ManualControlBlock.Busy,
        _ => ManualControlBlock.None,
    };
    public bool ManualControlsEnabled => ManualBlock == ManualControlBlock.None;
    public bool ManualOutputsEnabled =>
        _io.IsReady
        && !_operations.IsShuttingDown
        && ManualMode
        && SafetyReady
        && !IsError
        && !IsRunning;
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
        IsHoming = value;
        Changed?.Invoke();
    }

    internal void SetAutomaticRunning(bool value)
    {
        AutomaticRunning = value;
        Changed?.Invoke();
    }

    internal void SetBoltTestRunning(bool value)
    {
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
        Changed?.Invoke();
    }

    internal void ClearError()
    {
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

    private void NotifyChanged() => Changed?.Invoke();

    internal static bool IsSafetyInput(InputIo input) => input is
        InputIo.EmergencyStop1Pressed
        or InputIo.EmergencyStop2Pressed
        or InputIo.AutoMode
        or InputIo.Door1Open
        or InputIo.Door2Open
        or InputIo.Door3Open
        or InputIo.Door4Open
        or InputIo.Door5Open
        or InputIo.Door6Open
        or InputIo.AirPressureLow;

    private static bool AffectsMachineState(InputIo input) =>
        input == InputIo.ServoMainContactorOn
        || IsSafetyInput(input);

    private static bool IsFaulted(AxisState state) =>
        state.Alarm || state.Emergency;
}
