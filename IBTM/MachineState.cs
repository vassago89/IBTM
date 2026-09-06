using System;
using System.Collections.Generic;
using System.ComponentModel;
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

internal readonly record struct MotionReadiness(
    bool Homed,
    bool ServosOn,
    bool Faulted);

public sealed class MachineState
{
    private readonly MachineOptions _options;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly BufferStage _buffer;
    private readonly BoltTrainingSession _training;
    private readonly IMotionFeedback[] _allMotions;
    private readonly IMotionFeedback[] _enabledMotions;

    public MachineState(
        MachineOptions options,
        UnitSettings units,
        IIoService io,
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
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _buffer = buffer;
        _training = training;
        _allMotions =
        [
            pcbSupply.Feedback,
            pcbPlacement.Feedback,
            boltFastening.Feedback,
            inspectionGantry.Feedback,
        ];

        var enabledMotions = new List<IMotionFeedback>(4);
        if (units.PcbSupply || units.PcbPlacement)
        {
            enabledMotions.Add(pcbSupply.Feedback);
            enabledMotions.Add(pcbPlacement.Feedback);
        }

        if (units.BoltFastening)
        {
            enabledMotions.Add(boltFastening.Feedback);
        }

        if (units.Inspection || units.NgCarrierTransfer)
        {
            enabledMotions.Add(inspectionGantry.Feedback);
        }

        _enabledMotions = [.. enabledMotions];

        io.InputChanged += (input, _) =>
        {
            if (AffectsMachineState(input))
            {
                NotifyChanged();
            }
        };
        io.OutputChanged += (output, _) =>
        {
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
    }

    public event Action? Changed;

    internal MotionReadiness MotionReadiness
    {
        get
        {
            var homed = true;
            var servosOn = true;
            var faulted = false;
            foreach (var motion in _enabledMotions)
            {
                if (!motion.IsReady)
                {
                    return new(false, false, true);
                }

                foreach (var axis in motion.Axes)
                {
                    var state = motion.GetAxisState(axis);
                    homed &= state.Homed;
                    servosOn &= state.ServoOn;
                    faulted |= IsFaulted(state);
                }
            }

            return new(homed, servosOn, faulted);
        }
    }

    public bool Homed => MotionReadiness.Homed;
    public bool ServosOn => MotionReadiness.ServosOn;
    public bool Faulted => MotionReadiness.Faulted;
    public bool Ready
    {
        get
        {
            var motion = MotionReadiness;
            return ServoMainContactorOn
                   && motion.Homed
                   && motion.ServosOn
                   && !motion.Faulted;
        }
    }

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

    public bool ConveyorRunning => _conveyor.RunCommandOn;
    public MainConveyorState MainConveyorState => _conveyor.State;
    public bool SupplyInBufferArea => _buffer.SupplyInside;
    public bool BufferConflict => _buffer.Conflict;

    public bool IsRunning =>
        AutomaticRunning
        || BoltTestRunning
        || IsHoming
        || _training.IsRunning
        || ConveyorRunning
        || Array.Exists(_allMotions, static motion => motion.IsMoving)
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
    public bool ManualControlsEnabled =>
        CanOperate
        && ManualMode
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

    internal void SetError(MachineAlarm alarm)
    {
        if (Alarm == alarm)
        {
            return;
        }

        Alarm = alarm;
        Changed?.Invoke();
    }

    internal void ClearError()
    {
        Alarm = MachineAlarm.None;
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
