using System.ComponentModel;

namespace IBTM.Core;

public enum MotionGroup
{
    [Description("Supply Handler")]
    PcbSupply,

    [Description("Placement Handler")]
    PcbPlacement,

    [Description("Bolt Fastening")]
    BoltFastening,

    [Description("Inspection / NG Transfer")]
    Inspection,
}
