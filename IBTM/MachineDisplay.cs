using System;
using System.Collections.Generic;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;

namespace IBTM;

// A completed display read, never an authorization to operate the equipment.
public sealed record MachineDisplay
{
    public bool Available { get; init; }
    public Exception? ReadError { get; init; }
    public bool SafetyReady { get; init; }
    public bool MotionFaulted { get; init; } = true;
    public bool IsRunning { get; init; }
    public StartBlockReason StartBlock { get; init; } = StartBlockReason.MotionFault;
    public HomeBlockReason HomeBlock { get; init; } = HomeBlockReason.IoUnavailable;
    public bool IsHoming { get; init; }
    public bool AutomaticRunning { get; init; }
    public MainConveyorState ConveyorState { get; init; }
    public NgConveyorState NgConveyorState { get; init; }
    public bool BufferConflict { get; init; }
    public bool SupplyInBufferArea { get; init; }
    public bool PlacementInBufferArea { get; init; }
    public bool SupplyAtHandoff { get; init; }
    public bool CanSupplyEnter { get; init; }
    public bool EmergencyStopReleased { get; init; }
    public bool DoorClosed { get; init; }
    public bool AirPressureOk { get; init; }
    public bool AutoMode { get; init; }
    public MachineAlarm Alarm { get; init; }
    public string? AlarmDetail { get; init; }
    public string? AlarmMessage { get; init; }
    public bool ServoPowerOn { get; init; }
    public bool Homed { get; init; }
    public bool CanStart { get; init; }
    public bool CanHome { get; init; }
    public bool CanRaiseCylinders { get; init; }
    public IReadOnlySet<(MotionGroup Group, MotionAxis Axis)> HomeableAxes { get; init; } = new HashSet<(MotionGroup, MotionAxis)>();
    public ManualControlBlock ManualBlock { get; init; } = ManualControlBlock.MotionNotReady;

    public bool ManualControlsEnabled
    {
        get
        {
            return Available && ManualBlock == ManualControlBlock.None;
        }
    }

    public bool ManualSetupEnabled { get; init; }
    public bool SetupEditingEnabled { get; init; }
    public PcbPlacementState PlacementState { get; init; }
    public HeatSinkSlot? PlacementTarget { get; init; }
    public BoltFasteningState FasteningState { get; init; }
    public BoltTarget? FasteningBolt { get; init; }
    public InspectionStationState InspectionState { get; init; }
    public BoltTarget? InspectionBolt { get; init; }
    public HeatSinkSlot? InspectionPcb { get; init; }
    // Collision clearance only, not automatic-run or whole-machine readiness.
    public OutputBlockReason MainConveyorPathBlock { get; init; } = OutputBlockReason.StateUnavailable;

    public bool MainConveyorPathClear
    {
        get
        {
            return MainConveyorPathBlock == OutputBlockReason.None;
        }
    }

    public RepeatPhase RepeatPhase { get; init; }
    public int RepeatCycles { get; init; }
}
