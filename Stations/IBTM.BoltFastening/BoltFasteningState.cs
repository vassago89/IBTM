using System.ComponentModel;

namespace IBTM.BoltFastening;

public enum BoltFasteningState
{
    [Description("Waiting")]
    Waiting,

    [Description("Preparing first shooting point")]
    MovingToStandby,

    [Description("Fastening")]
    Fastening,

    [Description("Completing Carrier")]
    CompletingCarrier,

    [Description("Preparing Carrier Work")]
    PreparingCarrier,

    [Description("Disabled")]
    Disabled,
}
