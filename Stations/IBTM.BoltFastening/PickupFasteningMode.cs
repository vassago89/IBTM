using System.ComponentModel;

namespace IBTM.BoltFastening;

public enum PickupFasteningMode
{
    [Description("1-stage fastening")]
    SingleStage,

    [Description("2-stage fastening")]
    TwoStage,
}
