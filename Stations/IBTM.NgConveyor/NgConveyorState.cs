using System.ComponentModel;

namespace IBTM.NgConveyor;

public enum NgConveyorState
{
    [Description("Waiting: transfer release + pickup UP")]
    WaitingForTransferRelease,

    [Description("Lowering NG Shuttle")]
    LoweringShuttle,

    [Description("Raising NG Shuttle")]
    RaisingShuttle,

    [Description("Waiting for NG Carrier")]
    WaitingForCarrier,

    [Description("Waiting for NG Shuttle Down")]
    WaitingForShuttleDown,

    [Description("Moving to Position 1")]
    MovingToPosition1,

    [Description("Moving to Position 2")]
    MovingToPosition2,

    [Description("Waiting for NG Shuttle Up")]
    WaitingForShuttleUp,

    [Description("NG Conveyor Full")]
    Full,

    [Description("NG Carrier Ready to Eject")]
    ReadyToEject,

    [Description("Ejecting NG Carrier")]
    EjectingCarrier,

    [Description("Compacting NG Carriers")]
    CompactingCarriers,

    [Description("Remove Carrier · EJECT Next / COMPLETE to Resume")]
    WaitingForEjectConfirmation,

    [Description("Carrier Position Unknown")]
    CarrierPositionUnknown,

    [Description("Returning carrier to shuttle")]
    ReturningToShuttle,

}
