using System.ComponentModel;

namespace IBTM.PcbPlacement;

public enum PlacementRotationState
{
    [Description("Unrotated")]
    Unrotated,

    [Description("Between")]
    Between,

    [Description("Rotated")]
    Rotated,
}

public enum PlacementGripperState
{
    [Description("Open")]
    Open,

    [Description("Between")]
    Between,

    [Description("Closed")]
    Closed,
}

public enum PlacementPcbState
{
    [Description("No PCB")]
    None,

    [Description("PCB Detected")]
    Detected,

    [Description("PCB Secured")]
    Secured,
}
