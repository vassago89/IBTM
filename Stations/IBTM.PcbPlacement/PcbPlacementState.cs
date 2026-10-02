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

    [Description("Placing and Pressing PCB")]
    PlacingPcb,

    [Description("Moving to standby")]
    Retracting,

    [Description("Waiting for Supply Departure")]
    WaitingForSupplyDeparture,

    [Description("Disabled")]
    Disabled,

}
