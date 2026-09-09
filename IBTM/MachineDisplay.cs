using System;
using System.Collections.Generic;
using System.ComponentModel;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;

namespace IBTM;

public enum DryRunTarget
{
    [Description("NG Transfer")] NgTransfer,
    [Description("Inspection Route")] Inspection,
    [Description("Main Conveyor")] MainConveyor,
    [Description("PCB Return")] PcbReturn,
    [Description("PCB Round Trip")] PcbRoundTrip,
    [Description("NG Conveyor Round Trip")] NgConveyor,
    [Description("Bolt Route")] BoltRoute,
}

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
    public bool ConveyorRunning { get; init; }
    public MainConveyorState ConveyorState { get; init; }
    public bool NgConveyorRunning { get; init; }
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
    public bool ManualControlsEnabled => Available && ManualBlock == ManualControlBlock.None;
    public bool ManualSetupEnabled { get; init; }
    public OutputBlockReason ManualOutputBlock { get; init; } = OutputBlockReason.StateUnavailable;
    public PcbPlacementState PlacementState { get; init; }
    public HeatSinkSlot? PlacementTarget { get; init; }
    public BoltFasteningState FasteningState { get; init; }
    public BoltTarget? FasteningBolt { get; init; }
    public InspectionStationState InspectionState { get; init; }
    public BoltTarget? InspectionBolt { get; init; }
    public HeatSinkSlot? InspectionPcb { get; init; }
    public NgTransferState NgTransferDryRunState { get; init; }
    public NgTransferDestination NgTransferDestination { get; init; }
    public int NgTransferDryRunTransfers { get; init; }
    public bool InspectionDryRunReady { get; init; }
    public InspectionDryRunState InspectionDryRunState { get; init; }
    public InspectionRouteDirection InspectionDryRunDirection { get; init; }
    public int InspectionDryRunPasses { get; init; }
    public HeatSinkSlot? InspectionDryRunPcb { get; init; }
    public int? InspectionDryRunBolt { get; init; }
    public string? InspectionDryRunBarcode { get; init; }
    public bool? InspectionDryRunBoltPresent { get; init; }
    // Collision clearance only, not automatic-run or whole-machine readiness.
    public OutputBlockReason MainConveyorPathBlock { get; init; } = OutputBlockReason.StateUnavailable;
    public bool MainConveyorPathClear => MainConveyorPathBlock == OutputBlockReason.None;
    public MainConveyorDryRunState MainConveyorDryRunState { get; init; }
    public MainConveyorDestination MainConveyorDestination { get; init; }
    public int MainConveyorDryRunPasses { get; init; }
    public Enum PcbReturnState { get; init; } = IBTM.PcbReturnState.WaitingForCarrier;
    public Enum PcbReturnDestination { get; init; } = IBTM.PcbReturnDestination.HeatSink;
    public HeatSinkSlot? PcbReturnHeatSink { get; init; }
    public int PcbReturnCount { get; init; }
    public Enum PcbDryRunState { get; init; } = PcbDryRunDirection.Ready;
    public PcbDryRunDirection PcbDryRunDirection { get; init; }
    public HeatSinkSlot PcbDryRunHeatSink { get; init; }
    public int PcbDryRunCycles { get; init; }
    public NgConveyorDryRunState NgConveyorDryRunState { get; init; }
    public NgConveyorDestination NgConveyorDestination { get; init; }
    public int NgConveyorDryRunPasses { get; init; }
    public bool NgConveyorDryRunReady { get; init; }
    public bool BoltRouteReady { get; init; }
    public BoltRouteState BoltRouteState { get; init; }
    public BoltRouteDirection BoltRouteDirection { get; init; }
    public BoltTarget? BoltRouteTarget { get; init; }
    public FasteningPass? BoltRoutePass { get; init; }
    public int BoltRoutePasses { get; init; }
}
