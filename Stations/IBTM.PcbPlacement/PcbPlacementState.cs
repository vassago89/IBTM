using System.ComponentModel;

namespace IBTM.PcbPlacement;

public enum PlacementCylinderState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
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

    [Description("Raising Handler and Moving to Placement Y")]
    PreparingPlacement,

    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Picking PCB for Repeat")]
    PickingPcb,

    [Description("Placing and Pressing PCB")]
    PlacingPcb,

    [Description("Completing Carrier")]
    CompletingCarrier,

    [Description("Returning PCB to Supply")]
    ReturningToSupply,

    [Description("Waiting for Supply to Open")]
    WaitingForSupplyReceipt,

    [Description("Lowering PCB to Supply")]
    PresentingToSupply,

    [Description("Waiting for Supply Grip")]
    WaitingForSupplyGrip,

    [Description("Releasing PCB to Supply and Departing")]
    ReleasingToSupply,

    [Description("Waiting for Supply Departure")]
    WaitingForSupplyDeparture,

    [Description("Disabled")]
    Disabled,

    [Description("Waiting for Supply to Confirm Handoff Clear")]
    WaitingForSupplyClear,
}
