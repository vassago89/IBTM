using System.ComponentModel;

namespace IBTM.BoltFastening;

public enum BoltFasteningState
{
    [Description("Waiting")]
    Waiting,

    [Description("Preparing First Shooting Bolt / Safe Z")]
    MovingToStandby,

    [Description("Feeding and Fastening Shooting Bolt")]
    FasteningShooting,

    [Description("Picking and Fastening Pickup Bolt")]
    FasteningPickup,

    [Description("Completing Carrier")]
    CompletingCarrier,

    [Description("Preparing Carrier Work")]
    PreparingCarrier,

    [Description("Disabled")]
    Disabled,
}
