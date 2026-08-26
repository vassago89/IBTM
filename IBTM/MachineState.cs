using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection.Training;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using Microsoft.Extensions.DependencyInjection;

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
    ControlCommunication,

    [Description("Motion Unavailable")]
    MotionUnavailable,

    [Description("PCB Supply")]
    Supply,

    [Description("PCB Placement")]
    Placement,

    [Description("Pickup Bolt Feeder")]
    PickupBoltFeeder,

    [Description("Linear Bolt Feeder")]
    LinearBoltFeeder,

    [Description("Bolt Fastening")]
    BoltFastening,

    [Description("Inspection")]
    Inspection,

    [Description("PCB Buffer Conflict")]
    BufferConflict,

    [Description("Main Conveyor")]
    MainConveyor,

    [Description("NG Conveyor")]
    NgConveyor,
}

public sealed class MachineState
{
    private readonly MachineOptions _options;
    private readonly UnitSettings _units;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgConveyorLine _ngConveyor;
    private readonly BufferStage _buffer;
    private readonly BoltTrainingSession _training;
    private readonly IAxisMotion _pcbSupplyMotion;
    private readonly IAxisMotion _pcbPlacementMotion;
    private readonly IAxisMotion _boltFasteningMotion;
    private readonly IAxisMotion _inspectionGantryMotion;

    public MachineState(
        MachineOptions options,
        UnitSettings units,
        IIoService io,
        MainConveyor conveyor,
        NgConveyorLine ngConveyor,
        BufferStage buffer,
        BoltTrainingSession training,
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion)
    {
        _options = options;
        _units = units;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _buffer = buffer;
        _training = training;
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionGantryMotion = inspectionGantryMotion;

        io.InputChanged += (_, _) => Refresh();
        io.OutputChanged += (_, _) => Refresh();
        buffer.PositionChanged += OnBufferPositionChanged;
        pcbSupplyMotion.MovingChanged += _ => Refresh();
        pcbPlacementMotion.MovingChanged += _ => Refresh();
        boltFasteningMotion.MovingChanged += _ => Refresh();
        inspectionGantryMotion.MovingChanged += _ => Refresh();
        training.Changed += Refresh;
    }

    public event Action? Changed;

    public bool Homed => EnabledMotions().All(motion =>
        motion.Axes.All(axis => motion.GetAxisState(axis).Homed));
    public bool ServosOn => EnabledMotions().All(motion =>
        motion.Axes.All(axis => motion.GetAxisState(axis).ServoOn));
    public bool Faulted =>
        EnabledMotions().Any(motion =>
            motion.Axes.Any(axis => IsFaulted(motion.GetAxisState(axis))));
    public bool Ready => Homed && ServosOn && !Faulted;

    public bool EmergencyStopReleased =>
        !_io.GetInput(InputIo.EmergencyStop1Pressed)
        && !_io.GetInput(InputIo.EmergencyStop2Pressed);
    public bool DoorClosed =>
        !_io.GetInput(InputIo.Door1Open)
        && !_io.GetInput(InputIo.Door2Open)
        && !_io.GetInput(InputIo.Door3Open)
        && !_io.GetInput(InputIo.Door4Open)
        && !_io.GetInput(InputIo.Door5Open)
        && !_io.GetInput(InputIo.Door6Open);
    public bool AirPressureOk =>
        !_io.GetInput(InputIo.AirPressureLow);
    public bool AutoMode => _io.GetInput(InputIo.AutoMode);
    public bool ManualMode => !AutoMode;
    public bool DoorInterlockReady =>
        !_options.UseDoorInterlock || DoorClosed;
    public bool SafetyReady =>
        (!_options.UseEmergencyStop || EmergencyStopReleased)
        && (!_options.UseAirPressureInterlock || AirPressureOk);

    public bool IsError => Alarm != MachineAlarm.None;
    public bool AutomaticRunning { get; private set; }
    public bool IsHoming { get; private set; }
    public bool IsTraining => _training.IsRunning;
    public MachineAlarm Alarm { get; private set; }

    public bool ConveyorRunning => _conveyor.RunCommandOn;
    public NgConveyorState NgConveyorState => _ngConveyor.State;
    public bool SupplyInBufferArea => _buffer.SupplyInside;
    public bool PlacementInBufferArea => _buffer.PlacementInside;
    public bool BufferConflict => _buffer.Conflict;

    public bool IsRunning =>
        AutomaticRunning
        || IsHoming
        || IsTraining
        || ConveyorRunning
        || _pcbSupplyMotion.IsMoving
        || _pcbPlacementMotion.IsMoving
        || _boltFasteningMotion.IsMoving
        || _inspectionGantryMotion.IsMoving
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
        if (BufferConflict && Alarm == MachineAlarm.None)
        {
            Alarm = MachineAlarm.BufferConflict;
        }
        Changed?.Invoke();
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
        if (BufferConflict && Alarm == MachineAlarm.None)
        {
            Alarm = MachineAlarm.BufferConflict;
            Changed?.Invoke();
        }
    }

    private IEnumerable<IAxisMotion> EnabledMotions()
    {
        if (_units.PcbSupply || _units.PcbPlacement)
        {
            yield return _pcbSupplyMotion;
            yield return _pcbPlacementMotion;
        }

        if (_units.BoltFastening)
        {
            yield return _boltFasteningMotion;
        }

        if (_units.Inspection || _units.NgConveyor)
        {
            yield return _inspectionGantryMotion;
        }
    }

    private static bool IsFaulted(AxisState state) =>
        state.Alarm || state.Emergency;
}
