using System.ComponentModel;

namespace IBTM.NgConveyor;

public enum NgConveyorState
{
    [Description("Waiting for NG Transfer Release and Pickup Up")]
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

    [Description("Securing NG Conveyor Stopper")]
    SecuringEjectStopper,

    [Description("Compacting NG Carriers")]
    CompactingCarriers,

    [Description("Remove Carrier · Press EJECT COMPLETE")]
    WaitingForEjectConfirmation,

    [Description("Release EJECT / COMPLETE Buttons")]
    WaitingForEjectButtonRelease,

    [Description("Carrier Position Unknown")]
    CarrierPositionUnknown,

    [Description("Returning carrier to shuttle")]
    ReturningToShuttle,

}

public enum NgShuttleLiftState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}
