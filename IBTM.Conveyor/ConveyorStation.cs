using System.ComponentModel;

namespace IBTM.Conveyor;

public enum ConveyorStation
{
    [Description("PCB Placement")]
    PcbPlacement,

    [Description("Bolt Fastening")]
    BoltFastening,

    [Description("Inspection")]
    Inspection,
}
