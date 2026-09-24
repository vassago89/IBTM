using System.ComponentModel;

namespace IBTM.PcbSupply;

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

    [Description("Handing PCB to Placement")]
    HandingOff,

    [Description("Waiting for Placement Y Departure")]
    WaitingForPlacementClear,

    [Description("Waiting for Returned PCB")]
    WaitingForReturnedPcb,

    [Description("Preparing to Receive Returned PCB")]
    PreparingReturnReceipt,

    [Description("Waiting for Placement to Present PCB")]
    WaitingForReturnedPcbGrip,

    [Description("Gripping Returned PCB")]
    ReceivingReturnedPcb,

    [Description("Waiting for Return Handoff Clearance")]
    WaitingForReturnClear,

    [Description("Returning Above Pickup with PCB")]
    ReturningToPickup,

    [Description("Disabled")]
    Disabled,
}
