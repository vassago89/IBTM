using System.ComponentModel;

namespace IBTM.NgConveyor;

public enum NgConveyorState
{
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

    [Description("Acknowledging Eject")]
    AcknowledgingEject,

    [Description("Release EJECT / COMPLETE Buttons")]
    WaitingForEjectButtonRelease,
}

public enum NgShuttleState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Pickup Up")]
    WaitingForCarrierPickupUp,

    [Description("Waiting for Position 3")]
    WaitingForPosition3,

    [Description("Lowering NG Shuttle")]
    Lowering,

    [Description("Waiting for NG Conveyor")]
    WaitingForConveyor,

    [Description("Raising NG Shuttle")]
    Raising,

    [Description("Carrier Position Unknown")]
    CarrierPositionUnknown,
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

internal enum NgConveyorPosition
{
    Position1 = 1,
    Position2 = 2,
    Position3 = 3,
}
