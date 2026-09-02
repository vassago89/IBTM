using System.ComponentModel;

namespace IBTM.PcbPlacement;

public enum PcbPlacementState
{
    [Description("Waiting for Buffer PCB")]
    WaitingForBufferPcb,

    [Description("Raising Handler")]
    RaisingHandler,

    [Description("Raising Z")]
    RaisingZ,

    [Description("Unrotating for Buffer")]
    UnrotatingForBuffer,

    [Description("Opening IPM Gripper")]
    OpeningGripper,

    [Description("Lowering IPM")]
    LoweringIpm,

    [Description("Moving Above Buffer")]
    MovingAboveBuffer,

    [Description("Lowering to Buffer")]
    LoweringToBuffer,

    [Description("Lowering Handler")]
    LoweringHandler,

    [Description("Waiting for PCB Detection")]
    WaitingForPcbDetection,

    [Description("Applying Vacuum")]
    ApplyingVacuum,

    [Description("Closing IPM Gripper")]
    ClosingGripper,

    [Description("Waiting for Supply Handler")]
    WaitingForSupplyExit,

    [Description("Raising IPM")]
    RaisingIpm,

    [Description("Moving to Wait Position")]
    MovingToWaitPosition,

    [Description("Rotating for Placement")]
    RotatingForPlacement,

    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Moving Above Heat Sink")]
    MovingAboveHeatSink,

    [Description("Lowering to Heat Sink")]
    LoweringToHeatSink,

    [Description("Releasing Vacuum")]
    ReleasingVacuum,

    [Description("Recording PCB Placement")]
    RecordingPlacement,

    [Description("Pressing PCB")]
    PressingPcb,

    [Description("Completing Carrier")]
    CompletingCarrier,
}

public enum PlacementRotationState
{
    [Description("Unrotated")]
    Unrotated,

    [Description("Between")]
    Between,

    [Description("Rotated")]
    Rotated,
}

public enum PlacementCylinderState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

public enum PlacementGripperState
{
    [Description("Open")]
    Open,

    [Description("Between")]
    Between,

    [Description("Closed")]
    Closed,
}

public enum PlacementPcbState
{
    [Description("No PCB")]
    None,

    [Description("PCB Detected")]
    Detected,

    [Description("PCB Secured")]
    Secured,
}
