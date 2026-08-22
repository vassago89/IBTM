using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
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

    [Description("PCB Supply")]
    Supply,

    [Description("PCB Placement")]
    Placement,

    [Description("Bolt Fastening")]
    BoltFastening,

    [Description("PCB Buffer Conflict")]
    BufferConflict,
}

public sealed class MachineState
{
    private readonly MachineOptions _options;
    private readonly ProcessSettings _processes;
    private readonly IIoService _io;
    private readonly MainConveyor _conveyor;
    private readonly NgConveyorLine _ngConveyor;
    private readonly BoltFasteningStation _boltFastening;
    private readonly BufferStage _buffer;
    private readonly IAxisMotion _pcbSupplyMotion;
    private readonly IAxisMotion _pcbPlacementMotion;
    private readonly IAxisMotion _boltFasteningMotion;
    private readonly IAxisMotion _inspectionGantryMotion;

    public MachineState(
        MachineOptions options,
        ProcessSettings processes,
        IIoService io,
        MainConveyor conveyor,
        NgConveyorLine ngConveyor,
        BoltFasteningStation boltFastening,
        BufferStage buffer,
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion)
    {
        _options = options;
        _processes = processes;
        _io = io;
        _conveyor = conveyor;
        _ngConveyor = ngConveyor;
        _boltFastening = boltFastening;
        _buffer = buffer;
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionGantryMotion = inspectionGantryMotion;

        io.InputChanged += (_, _) => Refresh();
        io.OutputChanged += (_, _) => Refresh();
        buffer.PositionChanged += OnBufferChanged;
        pcbSupplyMotion.MovingChanged += _ => Refresh();
        pcbPlacementMotion.MovingChanged += _ => Refresh();
        boltFasteningMotion.MovingChanged += _ => Refresh();
        inspectionGantryMotion.MovingChanged += _ => Refresh();
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
    public bool SafetyReady =>
        (!_options.UseEmergencyStop || EmergencyStopReleased)
        && (!_options.UseDoorInterlock || DoorClosed)
        && (!_options.UseAirPressureInterlock || AirPressureOk);

    public bool IsError => Alarm != MachineAlarm.None;
    public bool AutomaticRunning { get; private set; }
    public bool IsHoming { get; private set; }
    public MachineAlarm Alarm { get; private set; }

    public bool ConveyorRunning => _conveyor.RunCommandOn;
    public bool BufferOccupied => _buffer.Occupied;
    public bool SupplyInBufferArea => _buffer.SupplyInside;
    public bool PlacementInBufferArea => _buffer.PlacementInside;
    public bool BufferConflict => _buffer.Conflict;

    public bool IsRunning =>
        AutomaticRunning
        || IsHoming
        || ConveyorRunning
        || _pcbSupplyMotion.IsMoving
        || _pcbPlacementMotion.IsMoving
        || _boltFasteningMotion.IsMoving
        || _inspectionGantryMotion.IsMoving
        || _boltFastening.FeederRunCommandOn
        || _ngConveyor.RunCommandOn;

    public bool CanOperate =>
        Ready
        && SafetyReady
        && !IsError
        && !BufferConflict;
    public bool ManualControlsEnabled =>
        CanOperate
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

    private void OnBufferChanged()
    {
        if (!BufferConflict || Alarm != MachineAlarm.None)
        {
            return;
        }

        Alarm = MachineAlarm.BufferConflict;
        Changed?.Invoke();
    }

    internal void SetAutomaticRunning(bool value)
    {
        AutomaticRunning = value;
        Changed?.Invoke();
    }

    internal void SetError(MachineAlarm alarm)
    {
        Alarm = alarm;
        Changed?.Invoke();
    }

    internal void ClearError()
    {
        Alarm = MachineAlarm.None;
        Changed?.Invoke();
    }

    private IEnumerable<IAxisMotion> EnabledMotions()
    {
        if (_processes.PcbSupply)
        {
            yield return _pcbSupplyMotion;
        }

        if (_processes.PcbPlacement)
        {
            yield return _pcbPlacementMotion;
        }

        if (_processes.BoltFastening)
        {
            yield return _boltFasteningMotion;
        }

        if (_processes.Inspection)
        {
            yield return _inspectionGantryMotion;
        }
    }

    private static bool IsFaulted(AxisState state) =>
        state.Alarm || state.Emergency;
}
