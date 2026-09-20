using System.ComponentModel;

namespace IBTM.BoltFastening;

public enum BoltFasteningState
{
    [Description("Waiting")]
    Waiting,

    [Description("Preparing First Shooting Bolt / Safe Z")]
    MovingToStandby,

    [Description("Feeding and Fastening Shooting Bolt")]
    FasteningPcb,

    [Description("Waiting for Shooting Feeder")]
    WaitingForShootingFeeder,

    [Description("Picking and Fastening Pickup Bolt")]
    FasteningPickup,

    [Description("Waiting for Pickup Feeder")]
    WaitingForPickupFeeder,

    [Description("Completing Carrier")]
    CompletingCarrier,

    [Description("Disabled")]
    Disabled,
}

public enum BoltCylinderState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

internal enum BoltEscapeState
{
    [Description("Forward")]
    Forward,

    [Description("Between")]
    Between,

    [Description("Backward")]
    Backward,
}
