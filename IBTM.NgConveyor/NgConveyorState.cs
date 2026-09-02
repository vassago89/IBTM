using System.ComponentModel;

namespace IBTM.NgConveyor;

public enum NgConveyorState
{
    [Description("Waiting for NG Carrier")]
    WaitingForCarrier,

    [Description("Moving Transfer to Carrier")]
    MovingTransferToCarrier,

    [Description("Lowering Transfer at Carrier")]
    LoweringTransferAtCarrier,

    [Description("Closing NG Transfer Gripper")]
    ClosingTransferGripper,

    [Description("Waiting for Carrier Grip")]
    WaitingForCarrierGrip,

    [Description("Raising NG Transfer")]
    RaisingCarrierTransfer,

    [Description("Moving Transfer to Shuttle")]
    MovingTransferToShuttle,

    [Description("Lowering Transfer at Shuttle")]
    LoweringTransferAtShuttle,

    [Description("Opening NG Transfer Gripper")]
    OpeningTransferGripper,

    [Description("Waiting for Shuttle Carrier")]
    WaitingForShuttleCarrier,

    [Description("Lowering NG Shuttle")]
    LoweringShuttle,

    [Description("Moving to Position 1")]
    MovingToPosition1,

    [Description("Moving to Position 2")]
    MovingToPosition2,

    [Description("Shuttle Down / Carrier Position Unknown")]
    CarrierBetweenPositions,

    [Description("Storing at Position 3")]
    StoringAtPosition3,

    [Description("Raising NG Shuttle")]
    RaisingShuttle,

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

    [Description("Acknowledging Eject")]
    AcknowledgingEject,

    [Description("Release EJECT / COMPLETE Buttons")]
    WaitingForEjectButtonRelease,
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
