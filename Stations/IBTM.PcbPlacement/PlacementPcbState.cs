using System.ComponentModel;

namespace IBTM.PcbPlacement;

public enum PlacementPcbState
{
    [Description("No PCB")]
    None,

    [Description("PCB Detected")]
    Detected,

    [Description("PCB Secured")]
    Secured,
}
