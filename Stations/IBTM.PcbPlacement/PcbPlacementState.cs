using System.ComponentModel;

namespace IBTM.PcbPlacement;

public enum PcbPlacementState
{
    [Description("Preparing / moving to handoff")]
    MovingToHandoff,

    [Description("Receiving PCB")]
    ReceivingPcb,

    [Description("Waiting for Supply Release")]
    WaitingForSupplyRelease,

    [Description("Raising / leaving handoff")]
    PreparingPlacement,

    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Picking PCB for Repeat")]
    PickingPcb,

    [Description("Placing and Pressing PCB")]
    PlacingPcb,

    [Description("Moving to standby")]
    Retracting,

    [Description("Returning PCB to Supply")]
    ReturningToSupply,

    [Description("Waiting for Supply Grip")]
    WaitingForSupplyGrip,

    [Description("Releasing PCB / leaving supply")]
    ReleasingToSupply,

    [Description("Waiting for Supply Departure")]
    WaitingForSupplyDeparture,

    [Description("Disabled")]
    Disabled,

}
