using System.ComponentModel;

namespace IBTM.PcbPlacement;

public enum PcbPlacementState
{
    [Description("Waiting for Supply PCB")]
    WaitingForSupply,

    [Description("Preparing and Moving to Handoff")]
    MovingToHandoff,

    [Description("Receiving PCB")]
    ReceivingPcb,

    [Description("Waiting for Supply Release")]
    WaitingForSupplyRelease,

    [Description("Raising Handler to Travel Height")]
    PreparingPlacement,

    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Picking PCB for Repeat")]
    PickingPcb,

    [Description("Placing and Pressing PCB")]
    PlacingPcb,

    [Description("Completing Carrier")]
    CompletingCarrier,

    [Description("Disabled")]
    Disabled,
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
