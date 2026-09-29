using System.ComponentModel;

namespace IBTM.Core;

public enum MotionGroup
{
    [Description("Supply Handler")]
    PcbSupply,

    [Description("Placement Handler")]
    PcbPlacementHandler,

    [Description("Bolt Fastening")]
    BoltFastening,

    [Description("Inspection Station")]
    InspectionGantry,
}
