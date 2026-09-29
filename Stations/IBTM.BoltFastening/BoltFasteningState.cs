using System.ComponentModel;

namespace IBTM.BoltFastening;

public enum BoltFasteningState
{
    [Description("Waiting")]
    Waiting,

    [Description("Preparing first shooting point")]
    MovingToStandby,

    [Description("Shooting: feed / fasten")]
    FasteningShooting,

    [Description("Pickup: pick / fasten")]
    FasteningPickup,

    [Description("Completing Carrier")]
    CompletingCarrier,

    [Description("Preparing Carrier Work")]
    PreparingCarrier,

    [Description("Disabled")]
    Disabled,
}
