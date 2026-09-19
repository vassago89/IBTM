using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbSupplyState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Exit")]
    WaitingForCarrierExit,

    [Description("Preparing Pickup / Standby")]
    MovingToPickup,

    [Description("Picking PCB")]
    PickingPcb,

    [Description("Securing PCB and Moving to Handoff")]
    MovingToHandoff,

    [Description("Waiting for Placement Handler")]
    WaitingForPlacement,

    [Description("Releasing PCB")]
    ReleasingPcb,

    [Description("Waiting for Placement Handler Up")]
    WaitingForPlacementLift,
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
