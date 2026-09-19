using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbSupplyState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Exit")]
    WaitingForCarrierExit,

    [Description("Moving to PCB 1 Standby")]
    MovingToPickup,

    [Description("Picking PCB")]
    PickingPcb,

    [Description("Securing PCB")]
    SecuringPcb,

    [Description("Raising for Pickup")]
    RaisingForPickup,

    [Description("Rotating for Handoff")]
    RotatingForHandoff,

    [Description("Moving to Handoff")]
    MovingToHandoff,

    [Description("Waiting for Placement Handler")]
    WaitingForPlacement,

    [Description("Releasing PCB")]
    ReleasingPcb,

    [Description("Waiting for Placement Handler Up")]
    WaitingForPlacementLift,

    [Description("Leaving Handoff in XY")]
    MovingFromHandoff,

    [Description("Unrotating for Pickup")]
    UnrotatingForPickup,
}

public enum PcbSupplyRotationState
{
    [Description("Unrotated")]
    Unrotated,

    [Description("Between")]
    Between,

    [Description("Rotated")]
    Rotated,
}

public enum PcbSupplyCylinderState
{
    [Description("Backward")]
    Backward,

    [Description("Between")]
    Between,

    [Description("Forward")]
    Forward,
}

public enum PcbSupplyPcbState
{
    [Description("No PCB")]
    None,

    [Description("PCB Detected")]
    Detected,

    [Description("PCB Secured")]
    Secured,
}
