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

    [Description("Handing PCB to Placement")]
    HandingOff,

    [Description("Waiting for Placement Y Departure")]
    WaitingForPlacementClear,

    [Description("Disabled")]
    Disabled,

}
